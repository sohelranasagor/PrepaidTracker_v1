# Prepaid Tracker (iOS + Android)

An unofficial .NET MAUI app — iOS and Android from one C# UI — for prepaid electricity
meter customers in Bangladesh. Add a meter once; the app fetches your balance from
**NESCO** or **DESCO**, works out how long it will last, and tells you what to recharge.

The iOS half is built to be sideloaded with a free Apple ID: no paid developer account,
no Mac, and nothing about you stored anywhere but the phone. (A free certificate expires
after 7 days, which is a harder limit than anything in this code — see
[Background sync](#background-sync-and-why-the-alert-doesnt-depend-on-it).) The Android
half builds a signed APK in CI.

---

## Table of contents

1. [Status at a glance](#status-at-a-glance)
2. [What the app does](#what-the-app-does)
3. [How each utility is reached](#how-each-utility-is-reached)
4. [The forecast model](#the-forecast-model)
5. [Failed recharges, and the manual token](#failed-recharges-and-the-manual-token)
6. [Architecture](#architecture)
7. [Access control and the Render backend](#access-control-and-the-render-backend)
8. [UI conventions](#ui-conventions)
9. [Background sync, and why the alert doesn't depend on it](#background-sync-and-why-the-alert-doesnt-depend-on-it)
10. [Building the IPA](#building-the-ipa)
11. [Installing on the phone](#installing-on-the-phone)
12. [Constraints that are not going away](#constraints-that-are-not-going-away)
13. [Working notes](#working-notes)
14. [Known unknowns](#known-unknowns)
15. [Bug history](#bug-history)
16. [Roadmap](#roadmap)
17. [Disclaimer](#disclaimer)

---

## Status at a glance

| Area | State |
|---|---|
| DESCO balance / customer info / recharge history / monthly usage | Endpoints confirmed against a public client library |
| DESCO **monthly** usage | Fixed: takes `monthFrom`/`monthTo`, not `dateFrom`/`dateTo`. Confirmed working on a real account |
| DESCO **daily** consumption | Endpoint name now corroborated by a public client; still returns nothing on account `41240695`. Not required for the daily chart — see below |
| Daily chart **without** any utility daily feed | Works for both providers from the app's own balance readings. Needs **3 distinct meter reads**; 4 gives 3 bars |
| NESCO balance / customer info / recharge history / monthly usage | Confirmed against a real account (`14024780`) |
| NESCO daily consumption | **Does not exist.** NESCO publishes monthly only |
| Multiple meters, both utilities | Done |
| Bengali + English | Done (words translated, digits stay Latin) |
| Offline (last snapshot cached) | Done |
| Low-balance alerts | Local notifications, shown **in-app on both platforms** (iOS via a foreground delegate). Fired on sync **and** pre-scheduled for the predicted run-dry date; for an already-low meter the next few mornings are queued so one still arrives with the app closed. Text is `A/C · name · ৳left · ~days`; per-account ids, so multiple meters don't collide. **Push is impossible**, see below |
| Per-account notification control | Each meter has its own on/off switch **and** its own ৳ / days thresholds, edited inline in the Profile → 🔔 sheet. Muting a meter cancels any OS-held reminder for it in the same frame — no waiting for the next sync. Thresholds default to zero-means-inherit, so the app-wide defaults still cover any meter the user hasn't customised; a `Meter.EffectiveLowTaka` / `EffectiveLowDays` computed property is what `Notifier` reads, never the raw field |
| Background refresh — iOS | `BGAppRefreshTask` + `BGProcessingTask`. Opportunistic; **no wakes at all once force-quit**. Cannot check a live balance in the background — only pre-scheduled alerts fire |
| Background wake targeting | `SyncSchedule` aims the next wake at `last read + 24h + 20m`, shared by both providers, with a backoff that scales when the portal posts late |
| Background refresh — Android | WorkManager (24h periodic net) **plus** a one-shot precision wake, **plus** an exact daily alarm (`SetExactAndAllowWhileIdle`) that fires through Doze with the app closed, re-armed on boot, and runs a *live* sync. Survives swipe-away and reboot; a Force Stop or OEM battery manager (Xiaomi, Oppo, Vivo) can still skip it |
| Failed-recharge reconciliation | Done. A failed recharge is credited to the live estimate on trust, then settled against the next meter read; when it turns out never to have reached the meter the app surfaces the STS token to enter by hand. Logic covered by executed test cases; **the banner has never been rendered** |
| Diagnostics panel | Removed from the UI. `ProfilePage.DiagText()` still exists — re-render it in four lines when a device needs debugging |
| Compiled or run on a device | **Partially.** `Providers`, `Forecast`, `Models`, `SyncSchedule`, `Reconcile` compile, and their logic is covered by executed test cases. The MAUI-dependent files parse without syntax errors but cannot link — nuget.org is unreachable from the authoring environment |

That last row matters. Every compile error so far was found by a human running
GitHub Actions, not by the model writing the code. Read carefully; assume nothing.

---

## What the app does

Four tabs, modelled on what a prepaid customer actually needs.

**Home** — a pinned green header carrying the greeting (customer name comes from
the utility), a settings gear, and the balance card: current balance with a live
estimate chip beside it when the portal's reading is stale enough to project past
(see [The forecast model](#the-forecast-model)), days-remaining pill (⚠ and red
when low), and how stale the reading is. Below, scrolling: three action tiles
(Recharge / Details / Advice), the month summary (spent · daily average ·
estimated bill), a spend chart, and recent recharges.

**Usage** — 7-day / 30-day / Year windows. Total, average, heaviest day, then the
chart, then full recharge history with success/failure badges and the payment
channel (BKASH, ROCKET…).

**Insights** — days-remaining hero, a concrete suggested recharge amount, today vs
7-day average vs projected month-end, and plain observations ("spending 27% less
than usual this month").

**Profile** — accounts (tap to switch, swipe to delete), language, notifications
(a per-account sheet: master switch, plus one row per meter with its own on/off
and its own ৳/days thresholds — a small shop can alert at ৳100 while a home
alerts at ৳500), tariff, recharge target, and a background-refresh explainer
that says what *this* platform actually does.

The **Diagnostics** card is gone from the UI. It was a developer panel — endpoint names,
probe results, raw field counts — and it made the settings screen read like a debugger.
`DiagText()` is still in `ProfilePage.cs` because it remains the only thing that can say
*why* a sync failed on a phone with no debugger attached; putting the card back is four
lines in `Render()`.

### The chart is painted, not laid out

`UI.BarChart` draws on a `GraphicsView` (`UI.BarsDrawable`). It used to build one
contentless `Border` per bar inside a `Star`-sized `Grid` column — which works on iOS and
**collapses on Android**: a `Border` with no `Content` desires 0×0, and when Android
measures that grid with an unconstrained width the `Star` columns fall back to that desired
width, so every bar arranges about a pixel wide. The chart rendered as a row of vertical
lines, on device only. A canvas has no measure pass to lose, and hit-testing a tap back to
a bar index becomes arithmetic rather than a gesture per `Border`.

Tap a bar and the callout moves to it: `৳ 1,930` over `June 2026` on a monthly chart, over
`2026-06-12` on a daily one. The callout is centred in the card and sized from the widest
label in the series, so it neither jumps nor resizes as you tap along. Two canvas traps
worth remembering: `Font.DefaultBold` silently paints nothing on the Android canvas, and
`DrawString` defaults to `TextFlow.ClipBounds` — a 15px line in an 18px box clips away to
nothing.

### The chart adapts to the data source

**The daily chart does not require a utility daily feed.** It is built from the
app's own balance readings when `snap.Days` is empty, which is always for NESCO and
currently also for DESCO. `Sync.Record` stores one reading per meter per sync, dated
by the meter's own `ReadingTime`, and consecutive readings give the spend between
them. The thresholds, measured rather than assumed:

| Distinct meter reads | Series rows | Home chart | Usage 7/30/Year pills | Day cell |
|---|---|---|---|---|
| 1 | 0 | no | no | — |
| 2 | 0 | no | no | — |
| **3** | 2 | **yes** | **yes** | Last full day |
| 4 | 3 | yes | yes | Last full day |

Two reads produce only one interval, and a series shorter than two rows is
discarded — the guard against banking a single phantom ৳0 day. The card's
**"এ পর্যন্ত N বার আপডেট"** is exactly this counter, so it doubles as a progress
indicator for when the chart will appear.


- **DESCO** publishes daily BDT consumption → 7 daily bars with weekday initials.
- **NESCO** publishes only monthly totals → 6 monthly bars (Jan…Jun) with
  Latest / Average / Highest underneath.

Forced by the data, not a style choice. When a window is asked for that the data
can't support, the Usage tab substitutes monthly bars and says so, rather than
showing an empty screen.

A day the meter reports zero consumption (empty house, day trip, cyclone
shutdown) renders as a short `MinBarH`-tall green bar, not a placeholder —
placeholders mean "no data yet", zero bars mean "we know, and it was zero". The
distinction runs downstream: the chart shows them, `MonthSpent` counts them at
৳0, but rate math (EWMA, active daily average, LiveEstimate) skips them as noise
so a single vacation day doesn't drag the forecast toward zero for a week (see
[The forecast model](#the-forecast-model)).

#### Days the feed just skipped

DESCO's daily feed occasionally has holes in the middle — a Monday and Tuesday
missing between an otherwise-continuous string of days. Feeding that straight
into a bar chart drew seven consecutive bars as if the week had no gaps, and
the weekday strip read wrong (`F S S W T F S` when a real week ending Saturday
should show `S M T W T F S`). `UI.LastNCalendarDays(rows, n)` anchors on the
newest real date, walks back `n` calendar days, and draws a dashed
placeholder wherever the feed had no row for that day. The label under the
placeholder still shows its weekday letter — a missing Monday reads as "M"
under a dashed outline — so the calendar stays aligned.

`UI.PadLeft` is still the right tool for MONTHLY (yearly) charts and any
context where the slot count is decoupled from calendar spacing. Usage's
year view uses it too: a single synthesised month otherwise renders as one
bar filling the whole chart, with the stats collapsing to "Latest / Average /
Highest — all ৳549". Padding to 12 slots keeps the bar at a sensible height
with room for history to fill in on future syncs.

#### DESCO fallback vs NESCO fallback — same math, different message

When `Forecast.FromMonthly` is true, the rate came from
`monthlyTotal ÷ days` rather than a real daily feed. Two very different reasons
end up here:

- **NESCO** — portal design. NESCO has never published a daily figure and
  never will. `L.S("monthlyNote")` — *"NESCO reports monthly, not daily…"* —
  explains this once and the "+ Add reading" button offers the only lever a
  NESCO user has (two readings on separate days and a daily chart appears).
- **DESCO** — portal glitch. The utility's own daily endpoint returned
  something unusable (a schema we don't recognise, the sanity-check discard
  above, or literally no rows). `L.S("monthlyNoteFallback")` — *"Daily
  consumption data unavailable — rate calculated from the monthly total"* —
  says exactly that. **No "+ Add reading" button** on DESCO: DESCO users get
  their data from the utility API, not by hand, and surfacing the manual
  workflow would make a DESCO account read like a NESCO one. `Store.AddReading`
  is still callable if a DESCO user really wants to log a reading; the button
  just isn't the prompt.

---

## How each utility is reached

### DESCO — a real JSON API, unauthenticated

Base: `https://prepaid.desco.org.bd/api/tkdes/customer`
(a parallel `api/unified/customer` family exists for some meter types; we probe it too)

| Endpoint | Query | Returns |
|---|---|---|
| `getBalance` | `accountNo` | `balance`, `meterNo`, `currentMonthConsumption`, `readingTime` |
| `getCustomerInfo` | `accountNo` | `customerName`, `installationAddress`, `phaseType`… |
| `getRechargeHistory` | `accountNo`, `dateFrom`, `dateTo` (yyyy-MM-dd) | `rechargeDate`, `totalAmount` |
| `getCustomerMonthlyConsumption` | `accountNo`, `meterNo`, **`monthFrom`, `monthTo`** (yyyy-MM) | `month`, `consumedTaka`, `consumedUnit` |
| `getCustomerDailyConsumption` | `accountNo`, `meterNo`, `dateFrom`, `dateTo` (yyyy-MM-dd) | per-day `consumedTaka`, `consumedUnit` |

> **The monthly endpoint does not take `dateFrom`/`dateTo`.** It takes `monthFrom`
> and `monthTo`. Sending the wrong pair returns an empty payload — not an error —
> so the symptom is an account whose balance and recharge history work perfectly
> while the usage chart stays blank. Confirmed against two independent public
> clients (`mdminhazulhaque/python-desco`, `faisalnabil/desco-dashboard`).
>
> The **daily** window should end *yesterday*. A day's figure is posted once the
> day closes, and a range running into today comes back empty on some meters
> rather than merely omitting today.

Responses usually wrap the payload in `{ "data": … }` — but not always, and the
rows are sometimes nested one level inside it. `AsArray()` copes with both.
Field names differ between the two families, so read them with `FirstOf()` /
`FirstString()` rather than one hardcoded name.

Two things worth knowing:

- **DESCO's TLS chain is broken.** Public Python clients pass `verify=False`. We
  set `UseNativeHttpHandler=false` in the csproj so `HttpClient` uses
  `SocketsHttpHandler`, which accepts a `RemoteCertificateValidationCallback`.
  `NSUrlSessionHandler` does not. Nothing secret is transmitted — only a meter
  number, which is also printed on the meter.
- **`readingTime` is not "now".** DESCO reads the meter roughly once a day, so the
  balance can be up to 24 hours stale. Home displays that age. Don't substitute
  the sync time; that would imply a liveness the data doesn't have.

#### When the daily feed can't be trusted

The daily endpoint isn't publicly documented. We probe candidates and accept
whatever answers with a date-shaped field and a `consumedTaka`-shaped number.
On some meter types the endpoint that finally answers is not returning per-day
Taka increments at all — cumulative-from-month-start, kWh, or a different
scale entirely. When that happens, every downstream figure (daily average,
7-day chart, days-left, live estimate, month-end projection) inherits the
error, while `MonthSpent` — which comes from `getBalance.currentMonthConsumption`
— stays right. The card ends up showing two contradictory numbers ("Spent this
month ৳549" over a 7-day chart whose bars average ৳314/day).

`DescoProvider.FetchAsync` cross-checks the this-month sum of daily rows
against `currentMonthConsumption` (and, when the monthly endpoint answered,
against this month's monthly row). A large **over-report** — daily sum ≥ 2×
the reference — is the scale-mismatch signature; on that signal we discard the
daily feed and let `Forecast.Build` fall through to its monthly-total path.
Under-reporting is legitimate (a lagging feed, a missing day or two) and the
EWMA over the days we DO have is still meaningful, so we leave that case
alone. The trigger is logged as a `daily-sanity` line in **Profile →
Diagnostics** so you can tell when it fired.

#### Filling `Months` from `getBalance`

Some smart-meter accounts return `getBalance.currentMonthConsumption` but
their `getCustomerMonthlyConsumption` list doesn't include the running month
yet. `DescoProvider.FetchAsync` synthesises a `MonthRow` for this month from
`currentMonthConsumption` when the monthly list omits it — same authoritative
source, different endpoint. Without this, an account whose daily feed we
discarded AND whose monthly list lags one month would have an empty Usage tab
even though we hold the one number that matters. If the monthly endpoint
catches up on a later sync, the real row keyed on `Month == thisMonth`
replaces the synthesised one.

### NESCO — no API; a Laravel form

`https://prepaid.nesco.gov.bd/` redirects to
`https://customer.nesco.gov.bd/pre/panel`. **No login, no captcha.**

1. `GET` the panel, scrape `<meta name="csrf-token" content="…">`.
2. `POST` `{_token, cust_no, submit}` where `submit` is a Bengali label selecting
   which table comes back:
   - `রিচার্জ হিস্ট্রি` → customer info + recharge table
   - `মাসিক ব্যবহার` → monthly usage table
3. Parse the HTML.

**Recharge table columns** (confirmed against a real account):
`[9] amount`, `[11] channel (BKASH/ROCKET)`, `[12] date "01-JAN-2025 11:00 AM"`,
`[13] status (contains ব্যর্থ / fail when unsuccessful)`.

**Monthly table columns:** `[0] Year, [1] Month, [2] Recharge, [3] Discount, [4] Usage`.

**Customer fields** render as `<label>` + disabled `<input value="…">` pairs.
`LabelledFields()` walks the document in order pairing them, skipping `cust_no`,
`_token`, hidden inputs and buttons. `Pick()` matches the *label text* first
(`ব্যালেন্স`, `balance`, `স্থিতি`, `জের`) and falls back to a known index —
`-1` meaning "last numeric field", which is where the portal puts the balance.

> **Never parse this positionally without a label check, and never accept a
> balance equal to the submitted account number.** Both mistakes have shipped.
> See [Bug history](#bug-history).

Both portals mix Bengali numerals into their output. `Providers.Digits()`
normalises `০-৯` → `0-9` on every value before parsing.

---

## The forecast model

`Forecast.cs`. Three tiers, best first:

1. **Utility daily feed** (`FromUtility`) — DESCO's per-day taka. Available on the
   very first sync.
2. **Monthly total** (`FromMonthly`) — NESCO. Uses this month's running total if
   the portal has posted one; otherwise the **last complete month divided by that
   month's length** (`MonthIsPrevious`). The Home card then relabels itself
   "Spent last month" instead of implying the figure is current.
3. **Own readings** — daily spend derived from balances taken on different days.
   Interval spend = `previousBalance + rechargeSince − currentBalance`. Negative
   intervals are discarded (an off-book top-up). `rechargeSince` is bounded to
   `[prior.Date, readingDate)`, not "everything after the previous reading": NESCO's
   balance is a **midnight snapshot**, so a recharge dated *today* is not in today's
   balance yet. Counting it against today would inflate that one bar by the whole
   recharge amount and push the real day negative (see Bug history).

On top of the daily series:

- **EWMA**, `Alpha = 0.35`. Recent days weigh more, so a heat wave shows up within
  a day or two instead of being diluted by last month's mild weather. Zero-
  consumption days are skipped — see [Two "averages"](#two-averages-doing-different-jobs)
  below.
- **Weekday shape**, only after 21 days of history, only when that weekday has 3+
  samples, clamped to ±25% so one freak Friday can't distort the forecast.
- **Confidence** = `min(series.Count / 21, 1)`, shown as a bar under the
  days-remaining readout.
- **`Today` is nullable, and usually null.** NESCO never publishes a per-day figure
  and a DESCO day only appears once it closes, so "today's spend" is unknowable far
  more often than not. It used to be filled with the flat monthly rate, which made
  the Insights card compare the rate against itself and print "below average"
  unconditionally, every day, forever. When today isn't measured the cell falls
  back to **`LastFullDay`** — the newest day with a complete figure, under its own
  label — and shows `—` only when no daily data exists at all.
- **Monthly charts use `CompleteMonths()`.** The running month is a month-to-date,
  not a finished month: as a bar it draws short and reads as a collapse in usage,
  and it would drag the average and win "highest" comparisons it never entered
  fairly.
- **Suggested recharge** = `dailyRate × targetDays − balance`, grossed up by the
  **observed credit ratio**, rounded up to the nearest ৳50, and floored at the
  utility's published `MinRecharge` when it gives one.

  The gross-up used to assume 5% VAT. Both portals report what each recharge
  actually credited, and the real figure is **4.288%** — a ৳990 recharge credits
  ৳947.55, a ৳500 credits ৳478.56, on both utilities. `CreditRatio()` takes the
  **median** of the last six recharges: a recharge that also clears arrears credits
  far less (৳990 → ৳781.35 observed) and a mean would let that one outlier inflate
  every future suggestion. Falls back to the VAT constant when no recharge reports
  an energy amount.
- **Month-end estimate** = `monthSpent + dailyRate × time remaining`.
  `MonthSpent` prefers the utility's own `currentMonthConsumption` over summing our
  daily rows — the utility's figure includes what the daily feed rounds off.

  But that figure is measured **as of the meter's last read**, not as of now. Both
  portals read once around midnight, so by evening it is most of a day behind. The
  elapsed time is filled at the daily rate and the result is flagged estimated —
  the same walk-forward the live-estimate chip already applies to the balance,
  using the same 6-hour freshness threshold. Under six hours, the measured figure
  is left alone. Month-end projects from the read time itself, which is the one
  branch where that cutoff is known rather than guessed.
- **"Est. spent this month" is decided by COVERAGE, not by provider.** A month we
  hold data for from the 1st shows the plain label; a month we joined partway
  through, or whose elapsed days were filled at a rate, shows `~` and আনুমানিক.

### Live estimate — the "≈ ৳98 এখন" chip

The portal's balance is a midnight snapshot; wall-clock is not. Between meter
reads it can drift 12–36 hours behind. `Forecast.LiveEstimate` walks the last
measured balance forward, rendered as a small muted chip beside the big ৳ number:

    est = snap.Balance − rate × (elapsedHours / 24) + pendingRecharges

`pendingRecharges` = top-ups dated **after** `ReadingTime`. A recharge made during
day D shows up in the D+1 midnight balance, so it must be added back in the
meantime — same window as `Sync.Record`. Without this, a user who tops up in the
morning watches the chip keep sliding as if the money never arrived.

Top-ups the portal called **failed** are counted too, and the chip is marked
provisional while they are. See [Failed recharges, and the manual
token](#failed-recharges-and-the-manual-token) — that window is also what makes
the optimism self-expiring, so the guarantee below still holds.

The `rate` is not just the EWMA. Assuming today looks like your long-run average
when yesterday was actually quiet will confidently deduct spending that never
happened. Look at the last 3 completed days as a pattern (each "quiet" =
`spend < EWMA × 0.2`):

- **2 or 3 quiet** → vacation regime; return null and hide the chip. The measured
  figure IS the answer.
- **exactly 1 quiet** → one-off outlier (day trip, then home again); use the
  plain EWMA. This is the case the earlier single-day guard was too strict for —
  it kept the chip hidden for 24 hours after every single quiet day, even ones
  sandwiched between normal usage.
- **0 quiet** → normal life; blend `0.7 × yesterday + 0.3 × EWMA`. Yesterday
  dominates the near-term rhythm; the mean anchors the tail.

Fewer than 3 completed days on record → fall back to the single-day check.

Additional guards return null (chip hidden) for: no ReadingTime, portal reported
no balance (DESCO unified meters), reading under ~6 h old (measured IS the fresh
answer), and clock skew putting ReadingTime in the future.

**The sign of the estimate is a signal, not a filter.** An earlier version of
`LiveEstimate` returned null whenever the projection went non-positive, on the
reasoning that "the ⚠︎ recharge now pill already conveys that." Wrong. A user
already on emergency credit at midnight (`snap.Balance = -50`) burning another
৳25 over the day would see only the stale −৳50 with no signal that they were now
at −৳75, closer to the meter's cut-off. The current version returns the walked-
forward value regardless of sign; `BalanceLine` colours the chip by the story it
tells relative to the big number:

| Measured | Estimate | Chip colour | Meaning |
|---|---|---|---|
| +ve | +ve | muted grey | Normal. Both numbers agree in spirit. |
| +ve | −ve | red | Day's usage has pushed you into emergency credit even though the midnight snapshot still looks fine. |
| −ve | +ve | green | Recharge landed after the midnight read. Big red number is stale, chip is fresh good news. |
| −ve | −ve | red | Still on emergency, and the current debt is deeper than the midnight number shows. |

Sign detection happens **after** rounding, so a projection of −0.4 is not shown
as "≈ -৳0" in red. `Math.Round(est)` drives sign, colour, template, and displayed
digits together; the eye reads the same number the colour is claiming.

Never touches `Store.AddReading` and never drives `Notifier.CheckLowBalance`.
Alerts fire off measurements, not projections. Render-only, and every reopen
picks a fresh `DateTime.Now` — `StateToken()` includes a 5-minute wall-clock
bucket so `OnAppearing` forces a `Render()` even when nothing has synced.

### Two "averages", doing different jobs — and one adaptive cell

Two rates on the balance card look similar but answer different questions:

- **`ActiveAvg`** — flat mean over days that actually consumed. This is the
  "typical use" figure a user has in mind when they hear "daily average".
- **`DailyAvg`** — EWMA over the same active-day set. Drives the days-remaining
  pill, month-end estimate, suggested recharge, and the LiveEstimate blend.
  This is the rate that decides how fast the balance is depleting.

Both figures live in the same active-day world. A vacation day at ৳0 still
appears on the chart (as a `MinBarH` bar), still contributes ৳0 to `MonthSpent`,
and still counts toward the LiveEstimate 3-day quiet-pattern check. But rate math
skips it — a single trip should not tank the forecast rate for a week, and it
should not create the arithmetic contradiction of "৳58/day → 5 days" on the same
card (see Bug history).

`SimpleAvg` is a third figure kept for context: the flat mean *including* zero
days. It's what would answer "on average across every day of the week — quiet
ones included — what did I spend?". Never drives forecasts; only surfaced when
the user might benefit from seeing it alongside `ActiveAvg`, which is where the
adaptive cell comes in.

**The adaptive "দৈনিক গড় / Daily average" cell.** The month-summary and 7-day
chart TripleStats each carry an average cell that switches shape based on
whether the two figures differ:

- If `Math.Round(ActiveAvg) == Math.Round(SimpleAvg)` (a normal week with no
  zero days), the cell shows a single number — visually identical to the pre-
  split design. Nothing to disambiguate, no clutter.
- If they differ (any zero-consumption day in the window), the cell splits into
  two mini-rows under one shared label: `সব দিন ৳37` in muted grey above,
  `সক্রিয় দিন ৳58` in green below. The green figure is what drives the pill,
  bill, and recharge suggestion, so the eye lands on the "authoritative" number
  last.

The primitive that draws this — `UI.StackedCell` — lives in `App.cs` alongside
`UI.Cell` and `UI.TripleStatMixed`, the arbitrary-view variant of TripleStat that
lets a caller pass either the plain or split cell without touching the other two.
Existing `UI.TripleStat` (tuple-based) still works and now internally delegates
to `TripleStatMixed`, so nothing else changed.

The original design had `DailyAvg` include zeros so days-left "correctly"
reported the balance lasting longer during quiet stretches. Theoretically honest,
but produced a card where the two numbers on it didn't reconcile — you couldn't
divide balance by daily-average and get days-left. Users trust arithmetic they
can do themselves. Making both figures share one world was worth the small
pessimism in days-left; safer to over-warn than to overshoot. Adding the
adaptive split gets the transparency back without cluttering weeks when it
wouldn't help.

Nothing here is machine learning. The commercial app brands the equivalent screen
"AI চালিত"; behind it is an average and a division. Calling this "AI" would be a
lie told to the user, so the badge reads "From your usage".

---

## Failed recharges, and the manual token

"Failed" is the *portal's* word, and it misleads in both directions. Both utilities
mark a transaction failed when their own side of the handshake times out — by which
point the money has usually already left the wallet and a token has usually already
been minted. Three things can be true once that row appears:

- the token was generated **and** applied — the portal simply lied;
- the token was generated but never reached the meter — it is sitting in the
  transaction details waiting to be typed into the keypad;
- nothing happened at all, and the money will be reversed.

From the app's side the last two are indistinguishable, and both are answered the
same way: put the token on screen and tell the user to enter it. Only the meter can
settle which of the three it was, and it speaks once a day. `Reconcile.cs` is the
wait, the decision, and the record of it.

### Three states, not two

| State | Means | UI |
|---|---|---|
| `Unverified` | Portal said failed; no meter read dated *after* the recharge exists yet | pill "যাচাই হচ্ছে", chip amber with a dot, estimate credited |
| `Credited` | The next read rose the way a landed recharge makes it rise | pill "মিটারে পৌঁছেছে"; nothing else changes |
| `NotCredited` | It didn't | pill "টোকেন দিন", banner carrying the token, one notification |

`Unverified` is the state that matters and the easy one to skip. Collapsing it into
`NotCredited` would put "enter the token manually" on screen minutes after a
recharge, before a single piece of evidence exists — and a prompt that cries wolf on
day one is one nobody reads on day thirty. There is a window, usually most of a day,
where the honest answer is *we don't know yet*, and the UI says exactly that.

### The estimate is allowed to be optimistic; nothing else is

A failed recharge is added to `LiveEstimate` immediately, exactly like a successful
one. That is defensible only because of what the estimate is and isn't: it is
explicitly a projection, it is rendered as a small chip beside the measured number
rather than replacing it, and it **never** reaches `Store.AddReading` or
`Notifier.CheckLowBalance`. Alerts and history stay on measurements.

It is also self-expiring, which is the real safety argument. The window is
`r.Date >= readDate` — the same one the success path uses. A recharge dated D sits
in the estimate only until the read dated D+1 lands; from that moment it is *behind*
the read date and drops out of the sum whether it landed or not, because the
measured balance is now the answer either way. The optimism can therefore be wrong
for at most one meter-read cycle, and it corrects itself without needing the verdict
to be right.

While the estimate carries unverified money the chip turns amber and grows a dot,
with `incl. ৳478 unverified` underneath. An ordinary projection drifts by a fraction
of a day's burn; this one can be several hundred taka wrong in a single step. Those
are different kinds of number and shouldn't look alike. Red still wins over amber —
a negative estimate is the more urgent fact whether or not it's provisional.

### The decision

Run after the new reading is banked and the forecast rebuilt, since the burn rate is
an input:

    prior     last read at or before the recharge date — pre-recharge by construction
    after     first read strictly past it
    days      between prior and after
    burn      max(0, rate) × days
    others    energy of everything else in the window we already know landed
    energy    r.EnergyAmount, or r.Amount × CreditRatio(snap) when the portal is silent

    observed  = after.Balance − prior.Balance + burn − others

    landed    = observed >= energy × 0.5                     ← rule 1
    if energy > burn && after.Balance <= prior.Balance:       ← rule 2
        landed = false

**Rule 1** cuts at the midpoint between the two hypotheses — a landed recharge
leaves ≈`energy` unexplained, one that never arrived leaves ≈0. Half tolerates a day
that burned twice the average without ever mistaking consumption for a ৳478 credit.

**Rule 2** is the naive comparison — *did the balance actually go up?* — fenced to
the case where it is sound. It exists to catch a wild `burn`: a long gap between
readings, or an EWMA inflated by a heat wave, pushes `burn` up until rule 1 would say
yes to almost anything. A nine-day gap at ৳49/day contributes ৳441, which on its own
clears the ৳239 bar for a ৳478 recharge that plainly never arrived. But the raw
comparison is *meaningless* when the recharge is smaller than the interval's
consumption — a real ৳100 credit inside nine days of burn still leaves the balance
far below where it started — so the `energy > burn` precondition is not optional.
Both halves are covered by test cases; dropping either one breaks a real scenario.

`others` matters more than it looks. Two failed recharges bracketed by one pair of
readings will each "explain" the other's rise and both come out `Credited` off a
single credit, unless everything already known to have landed is subtracted first.

### Verdicts are written down, not recomputed

`verdicts-{id}.json`, keyed by `date|time|amount|via` — content, because the snapshot
is rebuilt from scratch on every fetch and there is no stable object or index to
hold on to. Capped at 60; the key is date-prefixed so an ordinal sort is a
chronological one and the oldest fall off first.

Persistence isn't tidiness, it's correctness. Both portals return a *rolling window*
of recharges, and the reading a verdict was decided against eventually rolls out of
the log too. Recomputing from whatever happens to be in the current snapshot would
let a settled row flip back to `Unverified`, re-fire its notification and re-raise
its banner days later. Decided once, never re-litigated — `Run` skips any row whose
state is not `Unverified`.

Dismissal is the user's. `Verdict.Dismissed` is set by an "I've entered it" button,
because the app cannot see a keypad and there is no honest way to auto-clear.

### It has to feed back into `Sync.Record`

The one non-obvious consequence. `Sync.Record` computes
`spent = prev.Balance + recharge − new.Balance`, and dropped a recharge the portal
called failed. So a failed recharge that *actually landed* made `spent` come out
negative, hit the "topped up off-book" guard, and **silently deleted that day from
the chart and the EWMA** — the same class of bug as the manual-reading prompt in the
history table below, arrived at from a different direction.

So `Record` now counts `r.Success || Reconcile.IsCredited(...)`, and `RunAllAsync`
re-runs `Record` when a verdict flips to `Credited`:

```csharp
if (Reconcile.Run(meter, result.Snapshot, f?.DailyAvg ?? 0))
{
    Record(meter, result.Snapshot);                                    // credit now counts
    f = Forecast.Build(Store.Readings(meter.Id), result.Snapshot);
}
```

That looks wasteful and isn't. The first `Record` had to run to bank the reading the
verdict is decided against; `AddReading` replaces by date, so the second is a
correction rather than a duplicate, and it only happens on the one sync that settles
a verdict.

### The token

"Enter the token manually" is useless advice on its own. `Reconcile.TokenOf` pulls
the 16–24 digit STS token out of the portal's own detail fields (matching `token` or
টোকেন on the key, digits only), and `Group` breaks it into fours — twenty unbroken
digits in a proportional font is not keyable, and losing your place means starting
the run again. When the utility published no token the banner says so and links to
the full transaction details instead of inventing something.

### Testing

The decision is four lines and every one has a way to be wrong. `Reconcile.cs`,
`Sync.cs`, `Forecast.cs` and `Models.cs` run headless against in-memory stands-in for
`Store`, `Notifier` and `IProvider`, driving the real `Sync.RunAllAsync`. Cases, all
passing:

| Scenario | Expect |
|---|---|
| prior ৳30, ৳500 failed, next read ৳478 | `Credited`; reading counts ৳500 so the day survives |
| prior ৳30, ৳500 failed, next read ৳14 | `NotCredited`; banner, token grouped, one notification |
| Five syncs the same day | banner once, notification once |
| Recharged today, no read past it | `Unverified` — no banner, chip provisional, estimate raised |
| Two failed ৳500 in one interval, balance rises by one | exactly one `Credited`, one `NotCredited` |
| Nine-day read gap, balance fell | `NotCredited` — rule 2 catches the inflated burn |
| Small ৳100 credit under nine days of burn | `Credited` — rule 2 correctly stays out of the way |
| Verdict settled, row leaves the portal window, returns | unchanged; nothing re-fires |
| Ordinary successful recharge | estimate credits it by exactly the same amount as a failed one; no verdict written |

What is **not** tested is the UI. `HomePage.cs` and `UsagePage.cs` parse clean and
their calls type-check, but nothing has been rendered — the banner layout, the amber
dot's alignment beside the balance, and the redraw after "I've entered it" are all
unverified on a device.

---

## Architecture

```
PrepaidTracker/
  PrepaidTracker.csproj       MAUI project (net8.0-ios;net8.0-android). Each CI job narrows
                              to one TFM, so neither needs the other's workload
  MauiProgram.cs              bootstrap
  App.cs                      Shell (4 tabs), Theme colors, shared UI builders
  Access.cs                   username claim, 5s block poll, Gate, BlockedPage
  Modal.cs                    AppPage base + in-app dialogs (no native popups)
  Models.cs                   Meter, Reading, RechargeRow, DayRow, MonthRow, Snapshot
  Store.cs                    per-meter JSON storage + Preferences-backed settings
  Providers.cs                IProvider, DescoProvider (JSON), NescoProvider (form scrape)
  Forecast.cs                 the usage model (see above)
  Reconcile.cs                failed recharges: optimistic credit, then settled against
                              the next meter read (see above)
  L.cs                        every user-facing string, English + Bengali
  Notifier.cs                 local alerts: fired-now, and scheduled-ahead (see below)
  Sync.cs                     the ONE sync + reading recorder. Foreground, iOS task and
                              Android worker all call it
  BackgroundSync.cs           iOS: BGAppRefreshTask + BGProcessingTask. Android: enqueues
                              the WorkManager periodic request
  HomePage.cs                 onboarding gate, pinned header, month summary, chart
  UsagePage.cs                7/30/365 windows, recharge history
  InsightsPage.cs             forecast, recharge suggestion, observations
  ProfilePage.cs              accounts, settings (DiagText kept, card not rendered)
  Platforms/iOS/              AppDelegate, Program, Info.plist
  Platforms/Android/          MainActivity, MainApplication, AndroidManifest,
                              Workers.cs (SyncWorker + AlertWorker)
  Resources/
    AppIcon/appicon_ios.svg   iOS: one opaque full-bleed square
    AppIcon/appicon_android.png + appicon_fg.png   Android: adaptive background + foreground
    Splash/splash.svg
    Images/tab_*.svg          tab bar icons; Resizetizer → tab_home.png etc.
server/                       access-control API (Node + Postgres, deployed to Render)
  server.js                   /v1/register, /v1/status, /admin/api/*
  admin.html                  the owner's block list
render.yaml                   Render blueprint: web service + Postgres
.github/workflows/build-ios.yml
global.json                   pins the .NET 8 SDK
```

**Storage.** Everything is per-meter and on-device: `readings-{id}.json`,
`snapshot-{id}.json`, `verdicts-{id}.json` in `FileSystem.AppDataDirectory`,
settings in `Preferences`.
Reads are memoised (`Store._cache`) and dropped on write — `StateToken()` and
`Render()` each ask for the same snapshot, so an uncached read meant a dozen
`File.ReadAllText` + `Deserialize` calls on the UI thread per account switch.

The only things that ever leave the phone are the meter number (to the utility,
over HTTPS) and — if access control is switched on — a username and a random
install id, to your own server. No balances, names or addresses go anywhere.

---

## Access control and the Render backend

Optional, and **off by default**. With `Access.ApiBase` empty the app behaves exactly as
it always did — no username screen, no polling, no server. Set it to your Render URL and
the gate switches on.

### How it works

On first launch the device claims a **unique username**. The server stores that name
against a random `InstallId` (a GUID in `Preferences` — not the account number, not the
customer's name). From then on the app asks the server **every 5 seconds while it is in
the foreground** whether that user is still allowed in.

The owner has two ways to shut someone out, and the difference matters:

| | What the device does | Reversible? |
|---|---|---|
| **Suspend** | The wall goes up. Meters, readings and history stay on the phone. | **Yes** — unblock and everything is back. |
| **Revoke + wipe** | The wall goes up *and* `Store.WipeMeters()` deletes every meter, reading, snapshot and update log. | **No.** The server never had that data, so nothing can restore it. The device starts over at onboarding. |

Both land within ~5 seconds (the next poll). Suspend is the default, and the destructive
one is a separate button behind a confirmation — locking someone out and destroying months
of their meter readings are different decisions, and the second should never be one mis-tap
away from the first.

`POST /admin/api/users/:username/block` takes `{ blocked: true }` to suspend and
`{ blocked: true, wipe: true }` to revoke. Unblocking clears the wipe flag, so a later
suspend can't silently re-wipe.

### Uninstalls, and freeing usernames

**A device cannot tell the server it has been uninstalled.** Neither iOS nor Android offers
an uninstall callback, and the phone is usually offline when it happens. Silence is the
only signal there is.

So the server reaps users that have gone quiet: `REAP_DAYS` (default **30**, `0` disables)
frees any username not seen for that long. It runs at boot and every 6 hours.

**Blocked users are never reaped.** This is the whole design of it. If the reaper touched
them, a block could be escaped by simply not opening the app for a month: the row would
vanish, the name would be free, and the device would re-register as unknown — and
unblocked. The reaper only removes *active* users.

Reaping doesn't hurt anyone who's still around. The app keeps the username in
`Preferences`, sees `known: false` on its next poll, and silently re-registers — the row
comes back. The only real loss is the name's *reservation*, and only if someone else
claimed it in the meantime (in which case that device gets a `403` and hits the wall, which
is correct: the name isn't theirs).

**What this does not do:** stop a blocked user from uninstalling, reinstalling, and
claiming a *new* username. A fresh install has a fresh `InstallId`, so it looks like a new
person. Closing that would mean an identifier that survives reinstall — the iOS Keychain
(which does), or Android's `ANDROID_ID` (which does). Ask if you want that; it's a
different, more invasive thing, and worth deciding deliberately.

### Uninstalls, and freeing usernames

**Neither iOS nor Android tells your server an app was uninstalled.** There is no callback,
no broadcast, no webhook — the app is simply never launched again. So "delete the username
on uninstall" can only ever be an approximation, and the honest one is: *a device that
hasn't polled in a long time is probably gone.*

`REAP_AFTER_DAYS` (default **60**, `0` disables) deletes any **unblocked** user whose last
poll is older than that. The sweep runs at boot and hourly; `POST /admin/api/reap` runs it
on demand.

Two guarantees make it safe:

* **A user who was merely away isn't harmed.** Their app still has the username in
  `Preferences`; the server replies `known: false`; `Access.CheckAsync` re-registers them
  on the next poll. They never notice. (And if someone else claimed the name meanwhile, the
  app clears it and sends them back to the username screen rather than carrying on with an
  identity the server doesn't recognise — a device with no server row is a device you can
  never block.)
* **A blocked user is never reaped**, however quiet they go. Freeing their row would hand
  the username back, and a blocked person who reinstalls is precisely the one who'd try.

#### Reinstalling

Uninstalling wipes `Preferences`, so a returning user has **no username and a brand-new
install id**. If the server refused to let them have their old name back, they'd be told
their own username was "taken" — by themselves — until the reaper freed it two months
later, and your admin list would fill with ghosts.

So `POST /v1/register` lets a new device **take over an UNBLOCKED name**. The row moves to
the new install id; the old one starts getting `403` and is gone. One row, no ghost.

A **blocked** name can't be taken over: the row is the record of the block, and a fresh
install must not inherit it. (An innocent person who happens to type a blocked name is
told it's taken, rather than being walled for someone else's sin.)

The cost of this rule: a stranger could squat an unused username. It buys them nothing — a
username carries no data and no privileges, and the server holds nothing worth stealing —
but it would stop the rightful owner from using that name.

#### What this cannot do

A blocked user who **uninstalls and reinstalls gets a fresh install id and picks a new
username** — so they're back in. Identity here is a random GUID in `Preferences`, and
`Preferences` dies with the app. There is no fix for this that doesn't involve a real
account (phone number, email, sign-in) or a device-attestation service. Worth knowing
before you rely on blocking as a security control rather than an administrative one.

### The two rules that keep this from backfiring

**Fail open when the server can't be reached, fail closed on a definite "no".** A network
error returns `null` and the app keeps the last known verdict. This matters more than it
sounds: a free Render service cold-starts for up to a minute, and a user who did nothing
wrong must not hit a wall because your server was asleep.

**The verdict is persisted.** `Store.Blocked` lives in `Preferences`, so a blocked user
can't escape by switching on airplane mode — the wall is already on their device.

A device using a username registered to a *different* install gets `403` and is treated
exactly like a block.

### API

| Route | Who | Does |
|---|---|---|
| `POST /v1/register` | app | Claim a username. Idempotent for the same device; `409 taken` if another device owns the name. |
| `GET /v1/status?username=&device=` | app | `{ known, blocked }`. `403` if the name belongs to another device. |
| `GET /admin/api/users` | owner | The list: username, platform, model, version, blocked, last seen. |
| `POST /admin/api/users/:username/block` | owner | `{ "blocked": true }` / `{ "blocked": false }`. |
| `DELETE /admin/api/users/:username` | owner | Frees the name so the person can start over. |

Owner routes want `Authorization: Bearer $ADMIN_TOKEN`. The admin page at **`/admin`**
asks for that token and gives you a list with a Block button per row.

### Storage

The server picks a backend from the environment at boot — no code change to switch:

| Set this | Backend | When |
|---|---|---|
| `DATABASE_URL` | **Postgres (Neon)** | The default. Free tier, doesn't expire. |
| `GITHUB_TOKEN` + `GIST_ID` | Secret GitHub gist | If you want no database at all. |
| neither | Memory | Dev only. Everything is lost when the process stops. |

**Not Render's own free Postgres** — it is *deleted* 30 days after creation. And not a file
on disk: a free Render service has an ephemeral filesystem, so local writes are wiped at
every spin-down. Neon's free tier runs indefinitely, which is why it's the default.

### Setting up Neon (the database)

Free, and unlike Render's own free Postgres it does not expire. Two minutes:

1. **Create a project** at [neon.tech](https://neon.tech).
   * **Neon Auth: OFF.** It's an authentication product for *your app's* users. This app
     has no user accounts, and the admin sign-in is our own. Leaving it on runs a
     schema-introspection loop that keeps the compute awake and eats the quota.
   * **Region:** the one nearest your *Render service*, not your users — the phone only
     ever talks to Render, and Render talks to Neon. Render free defaults to Oregon, so
     pick a US West region unless you moved it.
   * **Postgres version:** any. 17, 18, whatever it offers.
2. **Copy the connection string** from the dashboard. It looks like:

   ```
   postgresql://user:password@ep-something-123456.us-west-2.aws.neon.tech/neondb?sslmode=require
   ```

   Either the pooled or the direct string works — the server keeps its own small pool.
3. **Paste it into Render** as `DATABASE_URL`.

That's it. There is no migration step: `PgStore.init()` runs `CREATE TABLE IF NOT EXISTS`
on boot. To look at your data, use Neon's SQL editor:

```sql
SELECT username, platform, blocked, last_seen FROM users ORDER BY last_seen DESC;
```

#### What Neon's free plan gives you

| | Free plan | What it means here |
|---|---|---|
| Storage | 0.5 GB per project | A user row is ~200 bytes with its index → **~2.5 million users**. Irrelevant. |
| Compute | 100 CU-hours per project/month | **The only limit that can actually bite.** See below. |
| Scale to zero | after 5 idle minutes | The database sleeps. Waking it takes ~500 ms. |
| History window | 6 hours | Point-in-time restore reach. Fine for this. |

#### Scale-to-zero broke two things. Both are fixed.

**It used to crash the API.** When Neon suspends, it drops the connections `pg` is holding,
and `pg` reports that as an `error` event *on the pool*. An unhandled `error` event is
fatal in Node — so every time Neon went to sleep, the server died and Render restarted it.
`PgStore` now handles that event, keeps a small pool (`max: 3`) with a short idle timeout
so connections don't sit around going stale, and retries once if the first query after a
wake lands on a dead connection.

**It could have exhausted the quota.** 100 CU-hours at the 0.25 CU minimum is ~400 active
hours against a 730-hour month. The app polls every 5 seconds, so if those polls reached
Postgres the database would *never* sleep — **one person with the app open all day would
burn the month's compute**. Neon then suspends the project, the API can't reach the
database, and every device fails open: everyone you blocked is quietly unblocked.

So `PgStore` reads the whole table into memory at boot and answers **every poll from
memory**. Postgres is touched only when something *changes*. Measured with 50 users and a
full day of polling:

```
  boot (create table + load)   : 2 queries
  50 registrations             : 50 queries
  18,000 polls                 : 0 queries      <-- the point
  480 flush ticks              : 1 query
  ------------------------------------------
  a busy day                   : 53 queries
```

The database sleeps through the polling and the quota is never in play.

This is safe because Render's free tier runs **exactly one instance**, so every write goes
through this process and memory is authoritative. If you ever run two, that cache has to
become a short TTL or a `LISTEN`/`NOTIFY`.

#### Where this design actually runs out

Not at Neon. In order:

1. **Render bandwidth** (100 GB/mo free) — a poll is ~1 KB, so ~139,000 device-hours a
   month. At 30 min/day of use that's several thousand users.
2. **Render memory** (512 MB free) — the in-memory cache is ~350 bytes per user in V8, so
   the process OOMs somewhere north of 100,000 users.
3. **Render CPU** (0.1 free) — N devices with the app open is N/5 requests per second.

Neon is the least stressed thing in the system throughout. If you ever hit any of the
above, the fix is the **5-second poll**, not the database: raise the interval, return
`304 Not Modified` when nothing changed, and drop the full-table cache for a short TTL.

### Deploying to Render (free tier)

1. Push this repo to GitHub. Render → **New → Blueprint** → pick the repo. It reads
   `render.yaml` and creates the web service from `server/`.
2. Render prompts for the `sync: false` variables:
   * `DATABASE_URL` — the Neon connection string from above.
   * `ADMIN_PASSWORD` — your `/admin` password.

   `ADMIN_TOKEN` is generated for you; read it in **Dashboard → the service →
   Environment**.
3. Put the service URL in `Access.cs`:

   ```csharp
   public const string ApiBase = "https://prepaidtracker-api.onrender.com";
   ```

   Leave it `""` and the whole access-control feature stays dormant — no username screen,
   no polling, no server. Useful for building the app without the backend.
4. Rebuild the app. Sign in at `https://<your-service>.onrender.com/admin`.

Locally: `cd server && npm install && cp .env.example .env`, fill it in, `npm start`.

### What the free tier does, and what the app does about it

**Spin-down.** A free service sleeps after 15 minutes idle and cold starts in 30–60s.
Three consequences, all handled:

* The 5-second poll uses a **10-second timeout** and fails *quietly* — a sleeping server
  returns `null`, the last known verdict stands, and nothing on screen changes.
* Claiming a username and tapping **Check again** are the two calls a human is waiting on,
  so they use a **90-second** client and, after 6 seconds, say *"Waking the server — the
  first check of the day can take a minute."* A spinner alone reads as a broken app.
* A block therefore lands in ~5 seconds if any device has been active recently, and within
  a cold start if the server had gone to sleep.

**Instance hours.** 750 free hours per workspace per month against ~730 hours in a month.
The service only stays awake while some device is polling — i.e. while someone has the app
open — so in practice it sleeps overnight and you stay under. Don't add a 5-minute uptime
pinger "to avoid cold starts": that pins the service awake 24/7, burns the entire 750-hour
budget, and **suspends every free service in your workspace until the next month**.

**Ephemeral disk.** Free services lose all local file writes on restart. The server keeps
nothing on disk — everything is in Postgres — so this costs you nothing.

If the block list ever becomes load-bearing, the paid path is `starter` web ($7) +
`basic-256mb` Postgres ($6). The commented block at the bottom of `render.yaml` is that
configuration.

### Before you ship this

Polling a server every 5 seconds with a device identifier is *tracking*, and both stores
treat it as such. Declare it: Apple's privacy manifest and nutrition labels, Google Play's
Data Safety form. And be straight with the people using the app about who can switch it
off and why — they will find out the moment it happens.

---

## UI conventions

**Built in C#, not XAML.** Deliberate: fewer build-time surprises in CI, and every
page is a single `Render()` that rebuilds from state. Shared builders (`UI.Card`,
`UI.TripleStat`, `UI.BarChart`, `UI.Segmented`, `UI.Pill`, `UI.Primary`) live in
`App.cs` so the four pages look like one app.

**Every page derives from `AppPage`** (in `Modal.cs`), which gives it:

- a root `Grid`, so modals can be layered over content;
- `ShouldRender(token)` gating. Each page builds a token from the state its UI
  depends on (meter id, snapshot timestamp, reading count, language, thresholds).
  `OnAppearing` only rebuilds when the token changed. Without this, switching tabs
  tore down and rebuilt the whole tree every time — which read as a **flash**.
  After mutating state, call `Invalidate()` then `Render()` (pages wrap that as
  `Refresh()`).

**No native dialogs.** `DisplayAlert` / `DisplayPromptAsync` / `DisplayActionSheet`
are iOS system UI and cannot be styled. `Modal.cs` provides `Confirm`, `Info`,
`Prompt`, `Choose` and `AddAccount`, drawn as a scrim plus a card inside the page's
root Grid, fading and scaling in. Tap the scrim to dismiss.

**Numbers stay in Latin digits, in both languages.** `L.N()` is deliberately an
identity function. An account number in Bengali numerals can't be checked against
the one printed on the meter, and money is read faster in the digits people type.
Words get translated; digits don't.

**Except in notification bodies.** `L.NBn()` is the prose-context counterpart that
converts to Bengali digits in Bengali mode. The line drawn between the two is "is
this a token I might dial into a meter, or a word in a sentence?". Account numbers,
meter numbers, the balance on the balance card, chart labels, the days-left pill —
all `L.N`. Only the notification body's `f.Balance` and `f.DaysLeft` use `L.NBn`,
so the Bengali user sees `অবশিষ্ট ব্যালেন্স: ৳৮০ টাকা` on their lockscreen instead
of a script hop mid-clause. The account number in the notification's lead line
stays Latin because the whole point of that line is "which meter" — and that's the
Latin string on the meter.

**Home has a pinned header.** Root grid = `[Auto header][* scrolling body]`. The
green gradient band runs *behind the status bar*, so `HomePage` calls
`On<iOS>().SetUseSafeArea(false)` and pads its own content by `StatusBarPad`.
Every other page inherits `SetUseSafeArea(true)` from `AppPage`. Get this wrong and
the balance card collides with the clock.

**The balance stays `Theme.Text`.** Colouring the number amber when low made a
healthy figure look like an error state. The days-left pill carries the warning.

**Charts** use `Border` + `LinearGradientBrush` bars (lighter at the top, deeper at
the base, rounded), a floating callout on the newest bar, and a dashed mean line
labelled `গড়`. MAUI has no dashed `BoxView`, so `DashedLine()` lays out the dashes.

---

## Background sync, and why the alert doesn't depend on it

### When to wake: `SyncSchedule`

Both utilities read the meter **once a day** and publish that moment — NESCO in the
`(সময়ঃ …)` caption, DESCO in `getBalance`'s `readingTime`. Between two reads there
is nothing new to fetch: balance, month-to-date and recharge list are identical no
matter how often you ask. So the question is not "how often should we poll" but
"when does the number actually change".

A fixed period answers neither. It fires 24h after whenever it was first enqueued —
whenever the user first opened the app — so an install at 3pm syncs at 3pm forever,
fetching a figure that last moved fifteen hours earlier.

`SyncSchedule.NextDelay()` targets `last read + 24h + 20m`, earliest meter wins:

| Situation | Wakes in |
|---|---|
| Read at midnight, app opened 08:00 | 16.3h → 00:20 |
| Portal 30 min late | 45 min |
| Portal 3h late | 3h |
| Meter silent 5 days | 6h (capped) |
| No reading stored yet | 6h |
| Clock skew, read dated in the future | 24h (capped) |

The overdue backoff scales with lateness, so a dead meter can't become a polling
loop against a government portal.

Two platform notes. On **Android** the one-shot precision wake is *not* re-enqueued
from inside `SyncWorker`: enqueuing unique work with `REPLACE` cancels the worker
currently running under that name. It is armed on launch, with the 24h periodic as
the net for users who never open the app. `REPLACE` is safe here in a way it is not
for the periodic, because the target is absolute — recomputing on launch re-aims at
the same moment rather than pushing it a day out. On **iOS**, both requests aim at
the read rather than a low fixed floor; an earlier wake has nothing to fetch.

**What a missed read does and does not cost.** It does *not* delete the day. The
balance is cumulative, so the next read recovers everything consumed during the
gap and `Forecast` spreads it evenly across the missing days. What is lost is the
day-to-day *shape*, not the total. The one case where data really is lost is a
recharge landing mid-gap that the reading's `Recharge` field fails to capture —
then `spent` goes negative and the whole interval is dropped.


Neither platform gives a sideloaded app a scheduler it controls.

| | iOS | Android |
|---|---|---|
| Mechanism | `BGAppRefreshTask` (habit-tuned) + `BGProcessingTask` (idle/charging, usually overnight) | WorkManager, 24h periodic, network-constrained |
| Swipe away from the app switcher / Recents | **No more wakes** until the user opens the app | Still runs — the queue is on disk |
| Force stop (Settings → Apps) | n/a | **Nothing runs** until the user launches the app |
| Reboot | Resumes | Resumes |
| Never opened for a week | Wakes taper toward never | Runs, but App Standby demotes it |
| OEM battery managers | n/a | MIUI / ColorOS / Funtouch may treat swipe-away as force stop and never run it |

So background *fetch* is best-effort on both, and on some real phones it is nothing at
all. The alert therefore does not rely on it:

- **`Notifier.CheckLowBalance`** — the balance is low *right now*, at sync time. Fires
  immediately. On iOS a `UNUserNotificationCenterDelegate` (`ForegroundNotificationDelegate`,
  set in `AppDelegate`) is now in place, so this shows **even while the app is open** —
  without it iOS silently drops any notification whose trigger fires in the foreground, which
  on a sideloaded build is almost always (the only reliable syncs are the ones you trigger by
  opening the app). Android shows foreground notifications with no delegate. The once-a-day
  guard is marked *after* the notification posts, so a suppressed or OS-denied alert no longer
  burns the day's slot.
- **`Notifier.ScheduleLowBalance`** — hand the OS a dated notification and walk away. iOS
  delivers a `UNCalendarNotificationTrigger` **even if the app has been force-quit**; Android
  uses a WorkManager one-shot. For a *healthy* meter it queues one heads-up `LowDays` before
  empty. For a meter that is **already** low — where the predicted date is in the past, so the
  old code queued nothing and left only the open-the-app alert — it now pre-schedules the next
  three 9am reminders, so a background notification still arrives with no launch needed.
  Re-armed on every sync; keys are `low-{meterId}-{idx}`, so multiple meters and multiple
  reminders never overwrite each other.
- **Per-meter gate before either of the above.** Every notification path — the live check, the
  scheduled reminder, and the failed-recharge follow-up — first tests `meter.NotificationsEnabled`
  and skips the meter when it is `false`. Toggling a meter off from the Profile → 🔔 sheet also
  runs `Notifier.CancelFor(meterId)` in the same frame, which drops any reminder the OS is
  already holding for that meter — otherwise tomorrow's already-armed 9am banner would still
  ring for a meter the user just muted. Toggling back on doesn't re-arm anything; the next sync
  will schedule from a fresh forecast, which is safer than arming from stale numbers. Editing
  either threshold does re-arm (`Notifier.CancelFor` + `Notifier.ScheduleLowBalance` against a
  freshly-built `Forecast`), because the OS is otherwise still holding a reminder timed against
  yesterday's threshold and the change would appear not to take effect for hours.
- **Android exact alarm** (`Alarms.cs`) — the one mechanism that gets close to "arrives on its
  own." `AlarmManager.SetExactAndAllowWhileIdle` fires through Doze, even with the app closed,
  wakes the process, runs a real sync (a *live* balance, not a stale forecast), notifies if
  low, then re-arms tomorrow. A `BOOT_COMPLETED` receiver re-arms after a reboot. Needs
  `USE_EXACT_ALARM` / `SCHEDULE_EXACT_ALARM` / `RECEIVE_BOOT_COMPLETED`. This is *more* than a
  sideloaded iOS app can do — iOS cannot wake to check a live balance at all.
- **Notification identity and text** — the body reads
  `A/C <number> · <name> • Remaining balance: ৳<left> • Approximately <days> days left`
  (Bengali: `… • অবশিষ্ট ব্যালেন্স: ৳<left> টাকা • আনুমানিক <days> দিন চলবে`), with `•`
  separating top-level clauses and `·` staying inside the account/name lead so the eye can
  chunk the three ideas. `Notifier.Lead` builds the account/name prefix from the utility's
  `CustomerName` (falling back to the user's label). In Bengali mode, `f.Balance` and
  `f.DaysLeft` are rendered through `L.NBn` (Bengali digits) — everything else — including
  the account number in the lead — stays Latin via `L.N`. Android posts with a **stable
  per-content id** (FNV-1a over the text), not `Environment.TickCount`, so two accounts
  going low in the same sync each get their own notification instead of the second
  overwriting the first (see Bug history). Android's channel is `low-balance-v2` at
  `Importance.High` — a fresh id because Android freezes a channel's importance at creation.

The cost is honesty about what the queued alert is: a projection from average usage, so an
unusual week makes it early or late, and it cannot quote a live balance. A warning that
always arrives beats a precise one that never does.

**A free Apple ID makes this moot past day 7 anyway.** The provisioning profile expires,
the app stops launching, and iOS runs nothing for an app whose signature is dead. On a free
sideload the certificate is the binding constraint, not the scheduler.

---

## Building the IPA

No Mac required. GitHub Actions builds an **unsigned** IPA that you sign locally.

1. Push this folder to a GitHub repo, keeping the structure (including
   `.github/workflows/` and `global.json`).
2. **Actions** → **Build Unsigned iOS IPA (for Sideloadly)** → **Run workflow**.
3. Download the **PrepaidTracker-unsigned-ipa** artifact.

First run is slow — it installs the `maui-ios` workload and caches the SDK; later runs
reuse both, and the NuGet cache step speeds up restore. The workload-install step runs on
**every** build, not just cache misses: `actions/cache` saves `~/.dotnet` in its post-step
even when a job fails, so a run that set up the SDK but died before the workload landed
would poison the cache — every run after that hits it, skips a *gated* install, and fails
`NETSDK1147`. Running the install unconditionally is a no-op when the workload is already
present and self-heals a partial cache.

### Why `global.json` exists

`macos-latest` ships whatever .NET SDK is current (10.x and up), and MSBuild picks
it unless a `global.json` says otherwise. The .NET 10 SDK treats the
end-of-support `net8.0-ios` target as a **hard error** (`NETSDK1202`).

Three defences, all active:

- `global.json` pins SDK `8.0.400`; `setup-dotnet` reads it via `global-json-file`
  so the two can't drift.
- `CheckEolTargetFramework=false` in the csproj silences the check.
- A **"Verify the SDK"** workflow step prints `dotnet --version` and fails loudly if
  the pin ever stops working.

To move forward instead of pinning back, see
`PrepaidTracker/PrepaidTracker.net10.csproj.txt` — a .NET 10 csproj with the
switch-over steps in a comment. No C# changes needed; drop `global.json`, bump the
target framework in the workflow, and remove the pinned MAUI `PackageReference`
lines (the SDK supplies them).

### Non-obvious csproj settings

| Setting | Why |
|---|---|
| `UseNativeHttpHandler=false` | DESCO's TLS chain; needed for the cert callback |
| `CheckEolTargetFramework=false` | NETSDK1202 |
| `MauiImage Include="Resources\Images\*.svg"` | tab bar icons |

---

## Installing on the phone

1. [Sideloadly](https://sideloadly.io/) on the PC, iPhone connected by USB.
2. Drag `PrepaidTracker-unsigned.ipa` in, enter your Apple ID, Start.
3. iPhone → **Settings → General → VPN & Device Management** → trust the profile.
4. Open the app → pick DESCO or NESCO → type your account number → Continue.

Free Apple ID caveats: the app expires after **7 days** (re-sideload the same IPA
to renew), and at most 3 sideloaded apps at once.

Sideloadly rewrites the bundle ID, so the placeholder `com.example.prepaidtracker`
is fine as-is. The identifier in `BGTaskSchedulerPermittedIdentifiers` does **not**
have to match the bundle ID, so the rewrite doesn't break background refresh.

---

## Constraints that are not going away

**Push notifications are impossible here.** A sideloaded app on a free Apple ID has
no `aps-environment` entitlement. Apps that push you a low-balance warning
(PrepaidPro does) run a server that polls the utility on your behalf — which is why
their privacy policy says they store your meter number and a push token. This app
instead:

- checks on every sync (i.e. every time you open it), and
- registers a `BGAppRefreshTask` so iOS can wake it, fetch, and alert while closed.

Background refresh is **best-effort by design**. iOS schedules it opportunistically
based on how you use the app — typically a few times a day, never on a fixed clock,
and only if Background App Refresh is enabled. "Notify me at 8am, guaranteed"
requires push, which requires a server. That trade is deliberate.

**NESCO has no daily data.** Not a limitation of this app. Sync daily and the
reading-derived series fills in over time.

**DESCO's balance can be a day old.** Also not fixable from here. It's displayed.

---

## Working notes

*Read before changing anything.*

- **Do not "simplify" the NESCO parser back to positional indexing.** It was
  positional once and silently reported the account number as the balance.
  Label-first, position-as-fallback, plus an explicit not-found guard.
- **Do not set `ReadingTime` for NESCO.** It publishes no meter-read timestamp.
  Leaving it null makes `Forecast.Freshness()` fall back to sync time — the truth.
- **Do not sum daily rows for `MonthSpent` when the utility gives you
  `currentMonthConsumption`.** The utility's number is authoritative.
- **Do not use days-elapsed as the divisor when the monthly total is last
  month's.** Divide by that month's actual length, or the rate inflates wildly
  early in the month.
- **Do not remove the DESCO daily-feed sanity check** without a real reason.
  It fires only when the this-month sum is ≥ 2× the reference (`currentMonthConsumption`
  or this month's monthly row), which is the scale-mismatch signature, not a
  common under-report. Removing it re-opens the "Spent this month ৳549 / Daily
  avg ৳314" contradiction on affected accounts. If you loosen the threshold,
  keep both sides logged as a `daily-sanity` line so the Diagnostics probe log
  still tells you what happened.
- **Do not surface "+ Add reading" on DESCO.** It's the ONE lever a NESCO user
  has; on DESCO it makes an API-fed account look like it needs manual data
  entry. `Store.AddReading` stays callable — the button just isn't the prompt.
- **Do not paste the NESCO monthly hint on a DESCO fallback.** `monthlyNote`
  describes portal design (never publishes daily); `monthlyNoteFallback`
  describes a portal glitch (daily was unusable this sync). Same math on both
  sides, different explanation to the user. Pick with `meter.Utility`.
- **Never call `DisplayAlert` / `DisplayPromptAsync` / `DisplayActionSheet`.**
  Use `Modal.*`.
- **Never rebuild a page unconditionally in `OnAppearing`.** Gate on
  `ShouldRender(StateToken())` or the tab flashes.
- **Don't assume a DESCO endpoint returns a bare array.** `AsArray()` unwraps both
  `data: [...]` and `data: { something: [...] }`.
- **Watch for `CS0136`.** C# rejects a nested-block local whose name is used later
  at method scope, even though they never overlap at runtime. This has bitten once.
- **This project cannot be compiled in the authoring environment.**
- **The person testing this has real meters in Bangladesh.** When something looks
  wrong, ask for **Profile → Diagnostics → Copy**:

```
utility        DESCO
synced         09/07/2026 11:41
balance        627.73
readingTime    08/07/2026 12:15
monthToDate    219.28
daily rows     7
recharge rows  12
monthly rows   6
rate source    utility daily feed

endpoint probe:
getBalance                       ok
getCustomerInfo                  ok
getRechargeHistory               12 rows
getCustomerDailyConsumption      7 rows
getCustomerMonthlyConsumption    6 rows
```

The probe block is the important part: every DESCO endpoint tried, in order, and
what came back. `0 rows` = answered but empty. `rows unparsed` = answered with
field names we didn't recognise.

### Reference account behaviour

Account `14024780` exists on **both** utilities, with different customers
(DESCO: Anowar; NESCO: Md. Golam Warich). Don't assume an 8-digit number belongs to
one utility. A "not found" from one is not evidence it belongs to the other.

---

## Known unknowns

1. **Whether DESCO serves daily consumption for every meter type.** The endpoint
   *name* is no longer a guess: `faisalnabil/desco-dashboard` calls
   `getCustomerDailyConsumption?accountNo=…&meterNo=…&dateFrom=…&dateTo=…`, ending
   the window at yesterday. That matches the first candidate below. What is still
   unresolved is that on at least one real account (`41240695`) it returns nothing
   even with the corrected window — which may mean the meter type has no feed
   rather than that the request is wrong. `DescoProvider` still probes, in order:
   - `tkdes/customer/getCustomerDailyConsumption`
   - `unified/customer/getCustomerDailyConsumption`
   - `tkdes/customer/getCustomerDailyConsumptionInfo`
   - `tkdes/customer/getDailyConsumption`
   - `unified/customer/getDailyConsumption`

   and caches whichever answers. **To resolve:** open
   `prepaid.desco.org.bd/customer/` in Chrome, DevTools → Network → filter `api`,
   log in, watch the daily-usage chart request. Then hardcode it. If all five miss,
   the app degrades to the monthly path — only the 7-day bar chart disappears.

   Even a probe-hit isn't a guarantee the values are per-day Taka increments —
   see the sanity check under [How each utility is reached → When the daily feed
   can't be trusted](#when-the-daily-feed-cant-be-trusted). Whichever endpoint
   we pin, keep the sanity check as a downstream backstop unless we've verified
   the schema against a specific meter type.

2. **How PrepaidPro derives its daily average.** Their screenshot shows ৳1930 for
   last month and ৳55/day. 1930 ÷ 30 = 64.3, not 55. Another shows ৳1734.14 and
   ৳58/day (1734 ÷ 30 = 57.8 — that one *does* reconcile). We chose not to
   reverse-engineer a number we can't explain. Ours divides the month's total by
   that month's actual length. If real usage suggests theirs is closer, investigate
   — don't just copy a constant.

3. **NESCO monthly column `[4]`** is assumed to be *Usage* (taka). If the Year chart
   shows recharge amounts instead of consumption, it's `[2]` vs `[4]`.

4. **Whether the ×0.5 threshold in `Reconcile` is the right cut.** It is the midpoint
   between "landed" and "didn't", which is the defensible default when both
   hypotheses are equally likely — but they may not be. If failed-but-credited turns
   out to be the common case in the field, a lower bar (say ×0.35) would settle more
   rows correctly on the first read. **To resolve:** log `Verdict.Observed` against
   `Verdict.Energy` over a few real months and look at where the two clusters
   actually fall. Don't move the constant on a hunch — the cost of a wrong
   `NotCredited` is a wasted trip to the meter, and the cost of a wrong `Credited` is
   a user who thinks they have power and doesn't.

---

## Bug history

Each entry is a mistake that looked reasonable at the time. Keep the list.

| Symptom | Cause | Fix |
|---|---|---|
| Insights showed the same figure under "আজ" and "৭ দিনের গড়", and the analysis card said "below average" every single day | The monthly-fallback path set `f.Today = rate`, so the observation compared the rate against itself. `Today <= avg` was necessarily true | `Today` is `double?` and left null when nothing about today was measured; the observation is skipped, and the section heading with it |
| Yesterday's spend displayed under a "Today" label | `f.Today` fell back to `rateDays[^1].Taka` whenever no row was dated today — which for a once-daily feed is most of the day | Only a row actually dated today counts; otherwise `LastFullDay` under its own label |
| DESCO usage chart blank while balance and recharges worked | `getCustomerMonthlyConsumption` was called with `dateFrom`/`dateTo`. It takes `monthFrom`/`monthTo` and returns an empty payload for the wrong pair | Correct spelling first, old one as fallback; rows parsed and validated *before* an endpoint is accepted |
| A year of monthly history vanished after one failed sync | `SaveSnapshot` merged `Days` across syncs but overwrote `Months` wholesale | Months merge too — minus rows flagged `Estimated`, which are month-to-date figures that would otherwise harden into a completed month next month |
| One partial month charted as history: "Latest ৳549 / Average ৳549 / Highest ৳549" | The synthesised current-month row was treated as an ordinary bar | `CompleteMonths()` excludes the running month; Home and Usage both chart from it |
| Suggested recharge one ৳50 step too high | Grossed up by a hard-coded 5% VAT when both utilities actually deduct 4.288% | `CreditRatio()` learns the median ratio from recent recharges; falls back to the constant when no energy figure exists |
| "1 days left" | One English plural form for every number | Singular key chosen from the *rendered* string, so rounding can't reintroduce it |
| Days containing a recharge silently missing from the daily chart | The Usage tab's manual-reading prompt never asked for the recharge, so `spent = prev + 0 − new` went negative and hit the "topped up off-book" guard | One shared `Modal.AskReading` for both tabs |
| A day containing a recharge the portal called "failed" vanished from the chart | The recharge landed anyway — the portal's status was wrong — but `Sync.Record` counted only `r.Success`, so `spent = prev + 0 − new` went negative and hit the "topped up off-book" guard. Same failure as the manual-reading row above, reached from the other direction | `Record` counts `r.Success \|\| Reconcile.IsCredited(...)`, and `RunAllAsync` re-banks the reading when a verdict flips to `Credited` |
| A meter on emergency credit never alerted | `lowMoney` required `Balance > 0`, excluding the most urgent state there is | Gate on `BalanceKnown`, not on the sign |
| Escalating a block to a wipe never wiped | `Access.Apply` returned early when `Blocked` was unchanged, before reaching the wipe | Wipe evaluated before the no-change return |
| Every host's TLS errors accepted | The certificate callback returned `true` unconditionally, on a handler shared with NESCO | Exception scoped to `desco.org.bd` |
| Balance displayed as `14,024,780.00` | NESCO parser took the last numeric `<input>`. On a failed lookup the only input left is the search box, still holding the typed account number | Skip `cust_no`/`_token`/hidden/submit; match balance by label; reject any balance equal to the submitted account number |
| Valid NESCO account rejected as "not found" | Over-corrected: required a `<label>` containing ব্যালেন্স, which the portal doesn't always render | Label match **with** positional fallback; "not found" now means *zero* usable fields |
| DESCO recharge history empty | Code required `data` to *be* the array. Some endpoints nest it one level down, and field names differ between the `tkdes` and `unified` families | `AsArray()` unwraps either shape; `FirstOf`/`FirstString` try several names; every endpoint family is probed |
| Usage tab completely blank | 7/30-day windows needed a daily series NESCO never supplies, and DESCO's daily probe can miss | Falls back to monthly bars with "No daily data — showing monthly totals instead"; empty state offers manual entry |
| Month cards blank on NESCO | Forecast needed a daily series that NESCO never provides | Monthly fallback tier (`FromMonthly`) |
| Screen flashed on every tab switch | `OnAppearing` cleared and rebuilt the whole layout each time | `AppPage.ShouldRender(token)` |
| iOS system popups | `DisplayPromptAsync` etc. | `Modal.cs`: in-app scrim + card |
| Delete account needed a double-tap | Non-discoverable | `SwipeView` with a destructive right item |
| Header collided with the status bar | `SetUseSafeArea` never configured; content drew under the clock | `AppPage` opts in; `HomePage` opts out and pads by `StatusBarPad`, because its gradient is meant to run behind the bar |
| Bengali numerals on account numbers and money | `L.N()` converted every digit | `L.N()` is now identity — words translate, digits don't |
| Balance turned amber when low | Colouring the number made a healthy figure look alarming | Balance stays `Theme.Text`; the pill carries the warning |
| Flat, blocky chart bars | `BoxView` fills | `Border` + `LinearGradientBrush`; dashed, labelled mean line |
| Tab bar had no icons | Never set `ShellContent.Icon` | `Resources/Images/tab_*.svg` + `MauiImage` |
| "just now ago" | `L.S("ago", freshness)` double-suffixed a phrase that already read "just now" | `Freshness()` returns a complete phrase |
| ৳ wrapped onto its own line | Menlo has no taka glyph, so the label fell back mid-string | Taka sign in system font beside a Menlo number; size shrinks for long amounts |
| `NETSDK1202` in CI | `macos-latest` upgraded to the .NET 10 SDK; no `global.json` | Pin SDK 8.0.400 + `CheckEolTargetFramework=false` + a verify step |
| `CS0136: 'thisMonth'` | Nested-block local shadowed a later method-scope local | Renamed to `currentMonthDays` |
| Sync used a hidden WebView | Assumed the portal needed a captcha; it doesn't | Direct `HttpClient`. This is also what made background refresh possible |
| Sync icon spun the wrong way | Tabler's refresh glyph sweeps anti-clockwise; `RotateTo(360)` is clockwise, so the arrowheads were dragged backwards | `RotateTo(-360)` |
| Warning banner never showed the emergency credit | It does — but only when the figure is known. NESCO doesn't publish it and the Profile setting was empty. (The old build's hard-coded `99999` is what people remembered) | None needed. But `StateToken()` was missing `LowDays`, so changing the days threshold in Profile didn't repaint the card |
| Account switch lagged, half-faded sheet hung on screen | `Snapshot()`/`Readings()` re-read and re-parsed JSON on every call, twice per render; the switch fired three full-tree rebuilds; and `Modal.Close()` removed the overlay in the same UI turn as the rebuild, so iOS never painted the removal | Memoise reads in `Store`; one rebuild per switch; `Close()` yields a frame after `Remove()` |
| `actool error: None of the input catalogs contained ... an app icon set named "appicon"` | Renaming the icon to `appicon_ios.svg` renames the generated `.appiconset`. `Info.plist` still pointed `XSAppIconAssets` at `appicon.appiconset` | Point it at `appicon_ios.appiconset`. Rename the icon file, change this key |
| Chart bars rendered as thin vertical lines — **Android only** | Each bar was a contentless `Border` in a `Star`-sized `Grid` column. A `Border` with no `Content` measures 0×0, and Android's unconstrained-width measure pass makes `Star` fall back to the desired width | Paint the chart on a `GraphicsView`. Deleted `DashedLine()` (40 `BoxView`s standing in for a dashed stroke) with it |
| Callout showed the month but no amount | `DrawString` defaults to `TextFlow.ClipBounds`; a 15px line in an 18px rect clips to nothing. (`Font.DefaultBold` also paints nothing on the Android canvas — two bugs stacked on the same line) | `TextFlow.OverflowBounds`, a taller rect, and no font swap |
| Callout resized on every tap | Width was measured from the *selected* label — "May 2026" narrow, "January 2026" wide | Measure across every row; size once for the worst case |
| Recharge token unreadable | DESCO's receipt prints `1246-1502-1501-0810-5738`; the API returns one unbroken 20-digit run and we showed it verbatim. It gets typed into a meter keypad | Group in fours in `HomePage.DetailRows`. Tap any modal row to copy — the clipboard gets the bare digits, since a keypad has no `-` key |
| Recharge rows on Home did nothing | `RechargeRowView` only attaches a gesture when given one, and Home never passed `onTap`. `DetailRows` was private to `UsagePage`, so Home couldn't have opened the modal anyway | `onTap` on Home's rows; `DetailRows` moved to `HomePage`, shared |
| NESCO Usage: three window pills, one chart, wrong numbers | With no daily feed all three pills fell through to the same monthly bars, and the stats kept their day-words — "Total spend ৳8,230" over a *7-day* tab, where 8,230 was six months and the "highest day" was a month | No daily feed → no daily windows. Drop the pills, label the stats as months, 12 bars, and offer the manual reading that actually creates a daily series |
| NESCO meter showed ৳0 spent, ৳0/day, no bill, no days-left — with a year of history on file | The meter is read once a day, so two syncs either side of that read see the *same* balance: `spent = 0`, and one ৳0.00 day gets banked. One row is not zero rows, so `if (f.Series.Count == 0)` skipped the monthly fallback and `ewma` came out 0, nulling every forecast field | A derived series that says nothing (fewer than 2 rows, or zero total) is discarded so the monthly total can do its job |
| Failed recharges inflated the forecast — background wakes only | `Record()` existed twice and had drifted: `HomePage`'s copy filtered `r.Success`, `BackgroundSync`'s did not, so a failed ৳500 counted as credit in `spent = prev + recharge − new` | One copy, in `Sync.cs`. Foreground, iOS task and Android worker all call it |
| Profile promised iOS background refresh on Android | One `bgHint` string, and `BackgroundSync` was a pair of empty stubs on Android | Platform-specific text; WorkManager actually implemented |
| `CS0119: 'HomePage.Sync(bool)' is a method` | `HomePage` has a `Sync` method, so a bare `Sync.Record(...)` binds to it, not to the new class | Fully qualify: `PrepaidTracker.Sync.Record(...)` |
| `JAVA0000: Type androidx.collection.ArrayMapKt is defined multiple times` | `Work.Runtime 2.9.0.3` pulls `Collection.Jvm 1.4.x`; MAUI 8.0.100 resolves `Collection.Ktx 1.2.x`; both jars carry the class | Pin `Collection` / `Collection.Jvm` / `Collection.Ktx` together at **1.4.0.4** — `.0.3` trips `NU1605`, because `Work → Core 1.13.1.1` floors Collection at `.0.4` |
| `APT2260: resource mipmap/appicon not found` | The Android half of the same trap: `appicon_android.png` generates `mipmap/appicon_android`, but `AndroidManifest.xml` still asked for `@mipmap/appicon` / `@mipmap/appicon_round` | Point `android:icon` and `android:roundIcon` at `appicon_android` / `appicon_android_round`. **Renaming a MauiIcon file means editing BOTH `Info.plist` and `AndroidManifest.xml`** |
| One day's spend bar spiked to ~৳500 and the real recharge day showed nothing — NESCO | `rechargeSince` was *every* successful recharge dated after the previous reading, with no upper bound. A recharge made *during* a day is dated that day but only appears in the *next* midnight balance, so it was added to `spent` while the balance hadn't risen — inflating that bar by the whole recharge and pushing the next day negative (silently dropped) | Bound the window to `[prior.Date, readingDate)` in `Sync.Record`, matching the midnight-snapshot semantics |
| iOS notifications never appeared while the app was open | No `UNUserNotificationCenterDelegate` was set, so iOS suppresses any notification whose trigger fires in the foreground — and the immediate alert fires 2s after a sync, which on a sideloaded build is almost always the foreground | `ForegroundNotificationDelegate` returns `Banner \| List \| Sound` from `WillPresentNotification`, set on `UNUserNotificationCenter.Current.Delegate` in `AppDelegate` |
| Android alerts sat silently in the shade, never popped as a banner | Channel created at `Importance.Default` | New channel id `low-balance-v2` at `Importance.High` — Android freezes a channel's importance at creation, so a *new id* is required for the change to take effect |
| Two accounts going low at once → only one Android notification appeared | `mgr.Notify(Environment.TickCount, n)`: two posts in the same millisecond share an id, so the second replaced the first | Stable per-content id (FNV-1a of title+body). The body carries the A/C number, so each meter maps to its own slot; re-alerting one meter updates in place |
| An already-low meter got no background alert — only the open-the-app one | `ScheduleLowBalance` queued nothing when the predicted date (`runsOut − LowDays`) was already in the past | When already low, pre-schedule the next three 9am reminders (`low-{meterId}-{idx}`) so the OS still fires something with the app closed |
| Blocked notifications failed silently | `RequestAuthorization` / `RequestAsync` results were discarded, so the app never knew the OS was blocking it; the daily slot was also marked before the alert posted | `RequestPermissionAsync` returns the grant, Profile shows a "notifications are off" hint when denied, and the slot is marked only after `Show` |
| `NETSDK1147: workload ios must be installed` in CI, despite a workload-install step | The install was gated `if: cache-hit != 'true'`; a poisoned `~/.dotnet` cache (saved after SDK setup but before the workload landed) meant every run hit the cache and skipped the install | Run `dotnet workload install maui-ios` unconditionally; bump the `~/.dotnet` cache key once (`-v1` → `-v2`) to discard the broken cache |
| NESCO balance card only ever showed the midnight snapshot — user opening the app at 2 PM saw a number that was 14 hours behind their meter, with no way to guess what they had NOW | The portal itself is midnight-only, and the app surfaced it as-is. `Freshness()` said "14 ঘন্টা আগে" but nothing bridged the gap | `Forecast.LiveEstimate` walks the balance forward using an active-day rate blend and renders as a compact chip beside the big ৳ number. Hidden when the reading is fresh (< 6 h), when the anomaly guard trips, or when the projection goes non-positive. Render-only — never touches `Store.AddReading` or `Notifier` |
| Live-estimate chip stayed stale across app opens: closed the app at 9 AM showing `≈ ৳163`, reopened at 2 PM still showing `≈ ৳163` | `StateToken()` hashed only meter data and settings — no time. `OnAppearing` saw an unchanged token, `ShouldRender` returned false, `Render()` never ran, so `DateTime.Now` was never re-read. The math WAS live; the render pipeline wasn't | Bake a 5-minute wall-clock bucket (`yyyyMMddHH` + `Minute/5`) into the token. Every reopen past the bucket boundary now forces a rebuild — 288 buckets a day, coarse enough that tab switches inside a minute don't churn the layout |
| NESCO chart hid the day the user consumed nothing, even when the next midnight reading confirmed it — chart said "M T W T" (4 days) but reality was "M T W T F" with F at ৳0 | `Completed()` unconditionally dropped trailing ৳0 days. Correct rule for DESCO's daily feed (a lagging feed's trailing ৳0 usually means "not posted yet"), wrong for readings-derived series where a `DayRow` only exists because two readings bracketed the interval — the ৳0 is confirmed, not missing | `Completed()` gained a `dropTrailingZeros` parameter; the NESCO/readings caller passes `false`. The zero day now draws as a `MinBarH` bar (visually distinct from a placeholder's dashed outline) and downstream stats (`SimpleAvg`, `Today`, `MaxDay`, `MonthSpent`) all see it |
| LiveEstimate chip stayed hidden for a full 24 h after any single quiet day — user took a one-day trip, came back, still no chip | Single-day anomaly guard: `if (yesterday < ewma × 0.2) return null`. Fired identically for "one-off day trip" and "actual multi-day vacation" | Pattern check over the last 3 completed days. Two or three quiet → still vacation, hide. Exactly one quiet → one-off; use plain EWMA and show the chip. Zero quiet → blend 70% yesterday + 30% EWMA. Under 3 days of history → fall back to the single-day check |
| "দৈনিক গড় ৳58" and "5 দিন চলবে" sat on the same card, but `183 ÷ 58 ≈ 3`, not 5 — the two numbers directly contradicted each other | Display used `ActiveAvg` (skips zero days), days-left used `DailyAvg` EWMA that still included them. Both were internally consistent with their own definitions, but the user couldn't reconcile numbers on a single card | EWMA now iterates over active days only, so both `ActiveAvg` and `DailyAvg` live in one active-day world. Days-left, month-end, suggested-recharge and LiveEstimate all divide the balance by a rate the user can also see on the card. Safer overall — the old design implicitly assumed the past frequency of quiet days would continue, which a low-balance warning shouldn't assume |
| A user on emergency credit at midnight (`snap.Balance = -50`) burning another ৳25 over the day saw only the stale −৳50 in the big number, with no clue they were now at −৳75 and closer to the meter's cut-off | `LiveEstimate` filtered out negative projections: `return est > 0 ? est : null`. The reasoning at the time was "the ⚠︎ recharge now pill already conveys emergency", but the pill says nothing about *how deep* — a widening debt is the one number the user most needs to see | Return the walked-forward value regardless of sign. `BalanceLine` now colours the chip by the story it tells: muted grey when both agree, RED when the estimate is negative (either crossed during the day or deepening from a starting negative), GREEN when the measured is negative but a recharge has tipped the estimate positive. The old guard was a case of the code being polite about the wrong thing |
| Estimate of −0.4 rendered as "≈ -৳0 এখন (আনুমানিক)" in red — a minus sign and emergency-credit colour attached to a displayed zero | `BalanceLine` decided sign from the raw `est` double (`est < 0`) but let the `#,0` format specifier round the displayed value independently. For any projection in the interval `(-0.5, 0]` the two disagreed | Round first (`Math.Round(est)`), then let that single rounded value drive sign, chip colour, template selection AND the digits shown. What the eye reads now matches what the colour is claiming |
| Same 7-day window, different average across tabs: Home said ৳58, Usage said ৳58, Insights said ৳50 — no visible reason | HomePage and UsagePage had been migrated to the `ActiveAvg` (active-day mean) source, but `InsightsPage` kept reading `SimpleAvg` (flat mean including zeros). On any account with a quiet day in the window, the Insights number came in ~৳8 lower than the identical figure on the other two tabs | InsightsPage now reads `f.ActiveAvg ?? f.SimpleAvg`, falling back to the plain mean only when every day in the window was quiet (`ActiveAvg` is null then anyway). The "today vs average" observation also compares against `ActiveAvg`, so a normal day after a vacation stops being flagged as "above average" |
| Notification body read as a comma-list: `"A/C 14003679 · Md. Motaleb Mia · ৳80 বাকি · ~1 দিন"` — dense, three ideas glued with the same separator, no visual chunking | The `alertBody` template joined all clauses with the same `·` (middle dot) the account/name lead used internally. Users had to parse the whole string to find the amount they cared about | New format uses `•` (bullet) BETWEEN clauses and keeps `·` INSIDE the lead: `"A/C 14003679 · Md. Motaleb Mia • অবশিষ্ট ব্যালেন্স: ৳80 টাকা • আনুমানিক 1 দিন চলবে"`. Three distinct clauses, three distinct thoughts. `alertPredicted` updated to the same style so the two forms read as a family |
| Bengali-language user's notification mixed Bengali words with Latin digits — `"৳80 টাকা"` in a Bengali sentence read as a script-hop mid-clause | The `L.N(ascii)` helper is (deliberately) identity — account numbers and money on the balance card have to stay Latin to match the meter's printed digits and to type back into a recharge app. But notification bodies read as prose, and the same rule doesn't fit | New `L.NBn(ascii)` helper: converts to Bengali digits in Bn mode, identity in English. Applied ONLY in `Notifier.CheckLowBalance` for `f.Balance` and `f.DaysLeft`; account number in the lead stays Latin because it's compared to the meter's Latin digits. Balance card, chart, pill — all still use `L.N` |
| DESCO account: "Spent this month ৳549" and "Daily avg ৳314" sat on the same card. ৳314 × 7 ≈ ৳2,200 — more spent in one week than the whole month claims to have cost. Everything projected from the rate (`0 days left`, `≈ −৳260 now`, `Est bill ৳6,193`) inherited the error while `MonthSpent` itself stayed right | Two separate DESCO endpoints, only one of them documented. `getBalance.currentMonthConsumption` is authoritative Taka; the daily-consumption endpoint's schema isn't publicly documented, and the response for this account wasn't per-day Taka increments. The code accepted whatever answered without cross-checking against the authoritative figure | Sanity check in `DescoProvider.FetchAsync`: if the this-month sum of daily rows is ≥ 2× the reference (`currentMonthConsumption`, falling back to this month's monthly row), discard the daily feed. `Forecast.Build` then falls through to the same monthly-total path NESCO uses. Logged as `daily-sanity` in the probe log |
| DESCO 7-day chart labels read `F S S W T F S` when a real week ending Saturday would show `S M T W T F S` | DESCO's daily feed had holes in the middle — Mon Jul 13 and Tue Jul 14 were missing. `TakeLast(7)` grabbed 7 non-consecutive rows and drew them tight against each other. Old `PadLeft` only padded the leading edge, not interior gaps | New `UI.LastNCalendarDays(rows, n)` anchors on the newest real date, walks back `n` calendar days, inserts a dashed placeholder for each missing day, and lets the weekday letter print under the placeholder — a missing Monday reads as "M" under a dashed outline instead of the letter row shifting |
| DESCO account showed the NESCO monthly hint after a sanity-check discard: "নেসকো মাসিক তথ্য দেয়, দৈনিক নয়…" on a DESCO Home tab | `Forecast.FromMonthly` was true for both NESCO's design-mandated fallback and DESCO's portal-glitch fallback; both call sites read `L.S("monthlyNote")` — hardcoded to mention NESCO. Same math, wrong explanation on DESCO | Second string `monthlyNoteFallback` — "Daily consumption data unavailable — rate calculated from the monthly total." Callers on HomePage and InsightsPage pick with `meter.Utility` |
| DESCO Usage tab empty ("এখনো ব্যবহারের তথ্য নেই") on an account whose Home tab was showing ৳548.9 spent this month | Sanity check cleared `snap.Days`, and this account's monthly endpoint hadn't listed the running month yet, so `snap.Months` was empty for Usage's yearly view too. `getBalance.currentMonthConsumption` — the same figure Home was drawing — sat unused | `DescoProvider.FetchAsync` synthesises a `MonthRow` from `currentMonthConsumption` when the monthly list omits this month. Real row from a later sync replaces it (keyed on `Month == thisMonth`) |
| DESCO Usage tab showed "+ Add reading" and "A day or two more of data and forecast begins" after a sanity-check discard | The message and the button were NESCO-shaped ("waiting for user to log data"). On DESCO the utility isn't waiting on the user — the daily feed is broken; there's nothing to add | NESCO/DESCO split at every site: `noDaily` + button for NESCO, `monthlyNoteFallback` (no button) for DESCO. New `descoNoData` for the truly-empty case ("The utility hasn't returned usable data yet — try syncing again in a minute") |
| DESCO Usage yearly chart drew ONE fat bar filling the whole card, with stats degenerating to "Latest ৳549 / Average ৳549 / Highest ৳549" | Single synthesised month + no padding meant the one bar's `Taka / max` ratio was 1.0, so it filled the plot area | `UsagePage` now runs monthly `rows` through `UI.PadLeft(rows, 12)` before the chart. The one bar renders at a sensible relative height with 11 dashed placeholders for the months not yet on file. Stats stay unpadded, so they still reflect the real one row (redundant, but not wrong) |
| DESCO failed recharge showed a bright green "⚡ ৳ 312.36" beside a red FAILED pill — read as if part of the ৳500 reached the meter | The utility reports a would-have-been `energyAmount` on failed rows too. Rendering it with the same green treatment as successful rows collapsed the outcome distinction into the Status pill alone | The chip is back on failed rows, drawn in `Theme.Muted` (grey) instead of `Theme.Green`. Row structure stays consistent across the list; colour carries the outcome. The detail modal shows the electricity line for both — Status is the source of truth |

---

## Roadmap

Nothing committed — ideas in rough priority order.

- Pin the DESCO daily endpoint once observed in DevTools; delete the probe list.
- Home Screen widget (needs a Widget Extension target; check how Sideloadly handles
  extensions before starting).
- Export readings to CSV.
- Copy-to-clipboard on the manual-token banner, and a "still not working?" path to
  the utility's complaint number. Right now the token can only be read and retyped.
- Compare this month against the same month last year — the monthly feed already
  carries 12 months.
- kWh charts, not just taka, where the utility reports `consumedUnit`.
- Tariff-slab awareness. Bangladesh tariffs are stepped, so a flat ৳/unit is a
  simplification; deductions also include demand charge, meter rent and VAT.

---

## Disclaimer

Unofficial. Not affiliated with, or endorsed by, NESCO or DESCO. The app makes the
same requests a browser would, at the rate a person would. The kWh figure is an
estimate. Nothing here should be relied on to prevent a disconnection — check the
meter.
