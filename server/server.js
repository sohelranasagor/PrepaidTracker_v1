// Prepaid Tracker — access-control API.
//
// Three jobs, nothing more:
//   1. Let a device claim a unique username on first launch.       POST /v1/register
//   2. Tell a device whether it's still allowed in.                GET  /v1/status
//   3. Let the OWNER see the list and block/unblock a user.        /admin (token-gated)
//
// Deliberately stores no customer data: a username, a random install id, and a
// device/platform string for the owner's benefit. No account numbers, no balances,
// no names. If this database leaks, nobody's electricity account is exposed.

const crypto = require('crypto');
const express = require('express');
const { makeStore } = require('./store');

const app = express();
app.use(express.json());
app.set('trust proxy', 1);

// Two ways in, same authority:
//   * ADMIN_USER + ADMIN_PASSWORD  → the /admin sign-in form, which hands the browser a
//                                    signed, expiring session token.
//   * ADMIN_TOKEN                  → a static bearer token, for curl and scripts.
// ADMIN_TOKEN doubles as the HMAC secret for session tokens: rotate it and every open
// browser session dies with it, which is exactly what you want after a laptop goes missing.
const ADMIN_TOKEN = process.env.ADMIN_TOKEN || '';
const ADMIN_USER = process.env.ADMIN_USER || 'admin';
const ADMIN_PASSWORD = process.env.ADMIN_PASSWORD || '';
const SESSION_HOURS = 12;

if (!ADMIN_TOKEN) console.warn('!! ADMIN_TOKEN is not set — the admin routes are disabled.');
if (!ADMIN_PASSWORD) console.warn('!! ADMIN_PASSWORD is not set — the /admin sign-in form is disabled.');

// Constant-time compare, so a wrong password can't be found one byte at a time by
// timing the response.
function same(a, b) {
  const x = Buffer.from(String(a));
  const y = Buffer.from(String(b));
  return x.length === y.length && crypto.timingSafeEqual(x, y);
}

function mint(user) {
  const exp = Date.now() + SESSION_HOURS * 3600_000;
  const body = `${user}.${exp}`;
  const sig = crypto.createHmac('sha256', ADMIN_TOKEN).update(body).digest('base64url');
  return `${Buffer.from(body).toString('base64url')}.${sig}`;
}

function sessionValid(token) {
  const [b64, sig] = String(token).split('.');
  if (!b64 || !sig) return false;

  let body;
  try { body = Buffer.from(b64, 'base64url').toString(); } catch { return false; }

  const expected = crypto.createHmac('sha256', ADMIN_TOKEN).update(body).digest('base64url');
  if (!same(sig, expected)) return false;

  const exp = Number(body.split('.').pop());
  return Number.isFinite(exp) && Date.now() < exp;
}

// Failed sign-ins per IP. In memory on purpose: this resets when the free instance spins
// down, and that's fine — it exists to make guessing slow, not to be an audit log.
const attempts = new Map();
const WINDOW = 15 * 60_000;
const MAX_TRIES = 8;

function throttled(ip) {
  const a = attempts.get(ip);
  if (!a) return false;
  if (Date.now() - a.first > WINDOW) { attempts.delete(ip); return false; }
  return a.count >= MAX_TRIES;
}

function noteFailure(ip) {
  const a = attempts.get(ip);
  if (!a || Date.now() - a.first > WINDOW) attempts.set(ip, { first: Date.now(), count: 1 });
  else a.count++;
}

const store = makeStore(process.env);

// ---------------------------------------------------------------- reaping stale usernames
//
// NEITHER iOS NOR ANDROID TELLS YOU AN APP WAS UNINSTALLED. There is no callback, no
// broadcast, no webhook — the process is simply never launched again. So "delete the
// username when they uninstall" can only be approximated: a device that has not polled in
// a long time is *probably* gone, and its username is freed.
//
// The approximation is safe in both directions:
//
//   * A user who was merely away (holiday, dead phone, didn't open the app for weeks) is
//     NOT harmed. Their app still has the username in Preferences, the server answers
//     `known: false`, and Access.CheckAsync re-registers them on the spot. They notice
//     nothing.
//   * A BLOCKED user is never reaped, however quiet they go. Deleting their row would
//     hand them their username back — and a blocked person who reinstalls is exactly the
//     one who would try. The row stays as the record of the block.
//
// REAP_AFTER_DAYS=0 turns this off entirely.
const REAP_AFTER_DAYS = Number(process.env.REAP_AFTER_DAYS ?? 60);

async function reap() {
  if (!(REAP_AFTER_DAYS > 0)) return;
  try {
    const gone = await store.reapStale(REAP_AFTER_DAYS);
    if (gone.length) console.log(`reaped ${gone.length} stale username(s): ${gone.join(', ')}`);
  } catch (e) {
    console.error('reap failed', e);
  }
}

const NAME = /^[a-z0-9_]{3,20}$/;

// ---------------------------------------------------------------- device API

// Claim a username. Idempotent for the same device; 409 if another device owns the name.
app.post('/v1/register', async (req, res) => {
  const { username, device, platform, model, version } = req.body || {};

  if (typeof username !== 'string' || !NAME.test(username))
    return res.status(400).json({ error: 'invalid_username' });
  if (typeof device !== 'string' || device.length < 8 || device.length > 64)
    return res.status(400).json({ error: 'invalid_device' });

  try {
    const existing = await store.get(username);

    // Same name, different device. Two very different situations:
    //
    //   NOT BLOCKED → let the new device TAKE THE NAME OVER. This is the reinstall case:
    //     uninstalling wipes Preferences, so a returning user has a brand-new install id
    //     and would otherwise be told their own username is "taken" — by themselves —
    //     for however long REAP_AFTER_DAYS is set to. Handing the name over costs nothing:
    //     a username carries no data and no privileges, and the server holds nothing worth
    //     stealing. The only downside is that a stranger could squat an unused name.
    //
    //   BLOCKED → refuse. The row is the record of the block, and a new device must not be
    //     able to inherit it (nor should an innocent person who happens to type a blocked
    //     name get walled). Note this does NOT stop a blocked user reinstalling and simply
    //     choosing a DIFFERENT name — see the README. That hole needs real accounts.
    if (existing && existing.device_id !== device && existing.blocked)
      return res.status(409).json({ error: 'taken' });

    if (existing && existing.device_id !== device)
      console.log(`username ${username} taken over by a new device (reinstall?)`);

    const user = await store.upsert({
      username, device_id: device,
      platform: platform || null, model: model || null, app_version: version || null,
    });

    res.json({ ok: true, known: true, blocked: !!user.blocked });
  } catch (e) {
    console.error('register', e);
    res.status(500).json({ error: 'server' });
  }
});

// The 5-second poll. Cheap on purpose: one indexed primary-key lookup.
app.get('/v1/status', async (req, res) => {
  const username = String(req.query.username || '');
  const device = String(req.query.device || '');

  if (!NAME.test(username)) return res.status(400).json({ error: 'invalid_username' });

  try {
    const user = await store.get(username);

    // Unknown name (the owner deleted the row). The app re-registers itself.
    if (!user) return res.json({ known: false, blocked: false });

    // Right name, wrong device: someone typed a name that isn't theirs. Same wall.
    if (user.device_id !== device) return res.status(403).json({ known: true, blocked: true });

    store.touch(username);          // fire-and-forget; the poll must not wait on a write

    res.json({ known: true, blocked: !!user.blocked, wipe: !!user.wipe });
  } catch (e) {
    console.error('status', e);
    res.status(500).json({ error: 'server' });
  }
});

// ---------------------------------------------------------------- owner API

// Sign in with a username and password; get a token good for 12 hours.
app.post('/admin/api/login', (req, res) => {
  const ip = req.ip || 'unknown';

  if (!ADMIN_TOKEN || !ADMIN_PASSWORD)
    return res.status(503).json({ error: 'not_configured' });

  if (throttled(ip))
    return res.status(429).json({ error: 'too_many_attempts' });

  const { username, password } = req.body || {};

  // Check BOTH before answering, and answer the same way for either being wrong: a
  // response that distinguishes "no such user" from "wrong password" tells an attacker
  // which half they've already got right.
  const ok = same(username || '', ADMIN_USER) & same(password || '', ADMIN_PASSWORD);

  if (!ok) {
    noteFailure(ip);
    return res.status(401).json({ error: 'bad_credentials' });
  }

  attempts.delete(ip);
  res.json({ token: mint(ADMIN_USER), expiresInHours: SESSION_HOURS });
});

function owner(req, res, next) {
  const given = (req.get('authorization') || '').replace(/^Bearer\s+/i, '');
  if (!ADMIN_TOKEN || !given) return res.status(401).json({ error: 'unauthorized' });

  // Either a session token from the sign-in form, or the static token itself.
  if (sessionValid(given) || same(given, ADMIN_TOKEN)) return next();

  res.status(401).json({ error: 'unauthorized' });
}

app.get('/admin/api/users', owner, async (_req, res) => {
  res.json(await store.all());
});

// { blocked: true }               → SUSPEND. Wall only; the device keeps its data and gets
//                                    it all back when you unblock.
// { blocked: true, wipe: true }    → REVOKE. Wall + the customer list is deleted on the
//                                    device. IRREVERSIBLE: unblocking cannot bring it back,
//                                    because this server never had the data to restore.
// { blocked: false }               → unblock.
app.post('/admin/api/users/:username/block', owner, async (req, res) => {
  const blocked = req.body?.blocked === true;
  const wipe = req.body?.wipe === true;

  const found = await store.setBlocked(req.params.username, blocked, wipe);
  if (!found) return res.status(404).json({ error: 'not_found' });

  res.json({ ok: true, username: req.params.username, blocked, wipe: blocked && wipe });
});

// Frees the username so the person can start over on a new device.
app.delete('/admin/api/users/:username', owner, async (req, res) => {
  await store.remove(req.params.username);
  res.json({ ok: true });
});

app.get('/admin', (_req, res) => res.sendFile(__dirname + '/admin.html'));

// The only thing a human ever wants from this host is the block list, so send them there.
// /health stays separate and text-only — it's what Render's health check hits, and it must
// not depend on the page rendering.
app.get('/', (_req, res) => res.redirect('/admin'));

// Render pings this; it also gives you something to hit to warm a cold start.
app.get('/health', (_req, res) => res.json({ ok: true }));

const port = process.env.PORT || 3000;
store.init()
  .then(() => {
    app.listen(port, () => console.log('listening on ' + port));

    if (REAP_AFTER_DAYS > 0) {
      console.log(`reaping unblocked usernames silent for ${REAP_AFTER_DAYS}+ days`);
      reap();                                    // once at boot...
      setInterval(reap, 6 * 3600_000).unref();   // ...then every 6 hours. The free instance
                                                 // spins down constantly, so this really
                                                 // means "whenever it happens to be awake" —
                                                 // fine: it's garbage collection, not a deadline.
    } else {
      console.log('username reaping disabled (REAP_AFTER_DAYS=0)');
    }
  })
  .catch((e) => { console.error('failed to start', e); process.exit(1); });
