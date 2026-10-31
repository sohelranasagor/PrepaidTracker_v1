// Where the user list lives.
//
// Three backends, picked from the environment at boot. You need exactly one:
//
//   GIST   GITHUB_TOKEN + GIST_ID   No database. The list is a JSON file in a secret
//                                   GitHub gist. Free, permanent, and survives every
//                                   Render restart. Right choice for a few hundred users.
//
//   PG     DATABASE_URL             Postgres (Neon, Supabase, Render paid). Right choice
//                                   if you already have one or expect thousands of users.
//
//   MEMORY (neither)                Dev only. Everything is lost when the process stops —
//                                   which on Render's free tier is every 15 idle minutes.
//
// Why not "just write a file"? A free Render service has an EPHEMERAL disk. Local writes
// are wiped on every redeploy, restart and spin-down, so a users.json on disk would be
// silently empty by tomorrow morning. The gist is a file — it just isn't on this box.

const GIST_FILE = 'users.json';

// ---------------------------------------------------------------- gist backend

class GistStore {
  constructor(token, id) {
    this.token = token;
    this.id = id;
    this.users = new Map();
    this.dirty = false;
    this.writing = false;
  }

  headers() {
    return {
      Authorization: 'Bearer ' + this.token,
      Accept: 'application/vnd.github+json',
      'X-GitHub-Api-Version': '2022-11-28',
      'Content-Type': 'application/json',
    };
  }

  async init() {
    const res = await fetch(`https://api.github.com/gists/${this.id}`, { headers: this.headers() });
    if (!res.ok) throw new Error(`gist read failed: ${res.status} ${await res.text()}`);

    const gist = await res.json();
    const file = gist.files?.[GIST_FILE];

    // A gist you created with an empty file, or a brand new one — both fine.
    const raw = file?.truncated ? await (await fetch(file.raw_url)).text() : file?.content;
    const parsed = raw && raw.trim() ? JSON.parse(raw) : {};

    for (const [name, u] of Object.entries(parsed)) this.users.set(name, u);
    console.log(`gist store ready — ${this.users.size} user(s)`);

    // last_seen changes on every 5-second poll from every device. Writing that straight
    // through would mean thousands of GitHub API calls an hour (the limit is 5,000) to
    // record something nobody reads in real time. So: flush on a timer, and only if
    // something actually changed. Blocks and registrations don't wait — they call
    // flush() directly.
    setInterval(() => this.flush().catch((e) => console.error('flush', e)), 60_000).unref();
  }

  async flush() {
    if (!this.dirty || this.writing) return;
    this.writing = true;
    this.dirty = false;

    try {
      const body = JSON.stringify({
        files: { [GIST_FILE]: { content: JSON.stringify(Object.fromEntries(this.users), null, 2) } },
      });

      const res = await fetch(`https://api.github.com/gists/${this.id}`, {
        method: 'PATCH',
        headers: this.headers(),
        body,
      });

      // Put the flag back so the next tick retries rather than losing the write.
      if (!res.ok) {
        this.dirty = true;
        console.error('gist write failed:', res.status, await res.text());
      }
    } catch (e) {
      this.dirty = true;
      throw e;
    } finally {
      this.writing = false;
    }
  }

  async all() {
    return [...this.users.values()].sort((a, b) => new Date(b.last_seen) - new Date(a.last_seen));
  }

  async get(username) {
    return this.users.get(username) || null;
  }

  async upsert({ username, device_id, platform, model, app_version }) {
    const now = new Date().toISOString();
    const existing = this.users.get(username);

    const user = existing
      ? { ...existing, device_id, platform, model, app_version, last_seen: now }   // device_id moves on reinstall
      : { username, device_id, platform, model, app_version, blocked: false, wipe: false, created_at: now, last_seen: now };

    this.users.set(username, user);
    this.dirty = true;
    await this.flush();          // a registration is worth a write immediately
    return user;
  }

  async setBlocked(username, blocked, wipe = false) {
    const u = this.users.get(username);
    if (!u) return false;
    u.blocked = blocked;
    u.wipe = blocked && wipe;
    this.dirty = true;
    await this.flush();          // so is a block
    return true;
  }

  async remove(username) {
    this.users.delete(username);
    this.dirty = true;
    await this.flush();
  }

  // In memory only until the next timed flush. Losing a few minutes of "last seen" on a
  // restart costs nothing; hammering GitHub to avoid that would cost the block list.
  touch(username) {
    const u = this.users.get(username);
    if (!u) return;
    u.last_seen = new Date().toISOString();
    this.dirty = true;
  }

  async reapStale(days) {
    const cutoff = Date.now() - days * 86_400_000;
    const gone = [];

    for (const [name, u] of this.users) {
      if (u.blocked) continue;                       // NEVER reap a blocked user
      if (new Date(u.last_seen).getTime() < cutoff) gone.push(name);
    }

    for (const name of gone) this.users.delete(name);
    if (gone.length) { this.dirty = true; await this.flush(); }
    return gone;
  }
}

// ---------------------------------------------------------------- postgres backend

class PgStore {
  constructor(url) {
    const { Pool } = require('pg');
    this.pool = new Pool({
      connectionString: url,
      ssl: url.includes('localhost') ? false : { rejectUnauthorized: false },

      // Neon SCALES TO ZERO when idle: it suspends the compute and drops the connections
      // this pool is holding. Keep few of them, and don't hold them long enough to go
      // stale in the first place.
      max: 3,
      idleTimeoutMillis: 10_000,
      connectionTimeoutMillis: 15_000,   // a suspended Neon takes a moment to wake
    });

    // WITHOUT THIS THE PROCESS DIES. An error on an IDLE client is emitted on the pool,
    // and an unhandled 'error' event in Node is a fatal exception — so every time Neon
    // went to sleep, the API would crash and Render would restart it. It isn't an
    // emergency: the pool discards the dead client and the next query dials a new one.
    this.pool.on('error', (e) => console.warn('idle pg client dropped (neon suspend?):', e.message));

    // THE WHOLE TABLE, IN MEMORY. This is not an optimisation, it's what keeps the
    // project inside Neon's free quota.
    //
    // Neon Free gives 100 CU-hours/month and suspends the compute after 5 idle minutes.
    // At the 0.25 CU minimum that's ~400 active hours against a 730-hour month. Every
    // device polls /v1/status every 5 seconds, so if those polls hit Postgres, the
    // database NEVER gets to sleep: one person with the app open all day burns the
    // month's compute, Neon suspends the project, and every device then fails open —
    // i.e. everyone you blocked is quietly unblocked.
    //
    // So: read the table once at boot, answer every poll from memory, and touch Postgres
    // only when something actually CHANGES (a registration, a block). The database sleeps
    // through the polling and the quota is never in play.
    //
    // Safe because this is a single instance (Render free runs exactly one) and every
    // write goes through this process, so memory is authoritative. If you ever scale to
    // two instances, this cache has to become a short TTL or a LISTEN/NOTIFY.
    this.users = new Map();
    this.dirtySeen = new Set();
  }

  // One retry, because the FIRST query after a Neon suspend can land on a connection that
  // died while nobody was looking.
  async query(sql, params) {
    try {
      return await this.pool.query(sql, params);
    } catch (e) {
      if (!/terminat|ECONNRESET|Connection terminated|socket hang up/i.test(e.message || '')) throw e;
      console.warn('retrying after a dropped connection:', e.message);
      return await this.pool.query(sql, params);
    }
  }

  async init() {
    await this.query(`
      CREATE TABLE IF NOT EXISTS users (
        username    TEXT PRIMARY KEY,
        device_id   TEXT NOT NULL,
        platform    TEXT,
        model       TEXT,
        app_version TEXT,
        blocked     BOOLEAN NOT NULL DEFAULT FALSE,
        wipe        BOOLEAN NOT NULL DEFAULT FALSE,
        created_at  TIMESTAMPTZ NOT NULL DEFAULT NOW(),
        last_seen   TIMESTAMPTZ NOT NULL DEFAULT NOW()
      );
    `);

    // Deployments created before "suspend vs revoke" existed. Cheap and idempotent.
    await this.query('ALTER TABLE users ADD COLUMN IF NOT EXISTS wipe BOOLEAN NOT NULL DEFAULT FALSE');

    const { rows } = await this.query('SELECT * FROM users');
    for (const r of rows) this.users.set(r.username, r);
    console.log(`postgres store ready — ${this.users.size} user(s) cached`);

    // last_seen is the one field the polls change, and nobody reads it in real time.
    // Batch it: one UPDATE a minute, only for users actually seen.
    setInterval(() => this.flushSeen().catch((e) => console.error('flushSeen', e)), 60_000).unref();
  }

  async flushSeen() {
    if (!this.dirtySeen.size) return;

    const names = [...this.dirtySeen];
    this.dirtySeen.clear();

    try {
      await this.query(
        `UPDATE users SET last_seen = NOW() WHERE username = ANY($1::text[])`, [names]
      );
    } catch (e) {
      names.forEach((n) => this.dirtySeen.add(n));   // retry next tick rather than lose it
      throw e;
    }
  }

  async all() {
    return [...this.users.values()].sort((a, b) => new Date(b.last_seen) - new Date(a.last_seen));
  }

  async get(username) {
    return this.users.get(username) || null;         // memory: no compute burned
  }

  async upsert({ username, device_id, platform, model, app_version }) {
    const { rows } = await this.query(
      `INSERT INTO users (username, device_id, platform, model, app_version)
            VALUES ($1, $2, $3, $4, $5)
       ON CONFLICT (username) DO UPDATE
            SET device_id = EXCLUDED.device_id,   -- reinstall: the name moves to the new install id
                platform = EXCLUDED.platform,
                model = EXCLUDED.model,
                app_version = EXCLUDED.app_version,
                last_seen = NOW()
         RETURNING *`,
      [username, device_id, platform || null, model || null, app_version || null]
    );

    this.users.set(username, rows[0]);
    return rows[0];
  }

  // wipe=false → SUSPEND: the wall goes up, the device keeps its data, unblocking gives it
  // all back. wipe=true → REVOKE: the wall goes up AND the customer list is destroyed on
  // the device. Unblocking cannot undo that; nothing on this server ever had the data.
  async setBlocked(username, blocked, wipe = false) {
    const { rowCount } = await this.query(
      'UPDATE users SET blocked = $1, wipe = $2 WHERE username = $3',
      [blocked, blocked && wipe, username]
    );
    if (!rowCount) return false;

    const u = this.users.get(username);
    if (u) { u.blocked = blocked; u.wipe = blocked && wipe; }
    return true;
  }

  async remove(username) {
    await this.query('DELETE FROM users WHERE username = $1', [username]);
    this.users.delete(username);
    this.dirtySeen.delete(username);
  }

  touch(username) {
    const u = this.users.get(username);
    if (!u) return;
    u.last_seen = new Date();      // memory now, Postgres at the next minute
    this.dirtySeen.add(username);
  }

  async reapStale(days) {
    // Flush pending last_seen FIRST, or a user who polled 30 seconds ago but whose
    // timestamp is still sitting in memory could be reaped as "stale".
    await this.flushSeen();

    const { rows } = await this.query(
      `DELETE FROM users
             WHERE blocked = FALSE
               AND last_seen < NOW() - ($1 || ' days')::interval
         RETURNING username`,
      [String(days)]
    );

    for (const r of rows) { this.users.delete(r.username); this.dirtySeen.delete(r.username); }
    return rows.map((r) => r.username);
  }
}

// ---------------------------------------------------------------- memory backend

class MemoryStore extends GistStore {
  constructor() { super('', ''); }
  async init() {
    console.warn('!! No GITHUB_TOKEN/GIST_ID and no DATABASE_URL — using MEMORY.');
    console.warn('!! Every user and every block is lost when this process stops. Dev only.');
  }
  async flush() { /* nowhere to flush to */ }
}

function makeStore(env) {
  if (env.GITHUB_TOKEN && env.GIST_ID) return new GistStore(env.GITHUB_TOKEN, env.GIST_ID);
  if (env.DATABASE_URL) return new PgStore(env.DATABASE_URL);
  return new MemoryStore();
}

module.exports = { makeStore, GistStore, PgStore, MemoryStore };
