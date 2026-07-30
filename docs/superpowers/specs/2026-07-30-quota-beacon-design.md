# QuotaBeacon design

Status: Approved for implementation

Date: 2026-07-30

Supersedes nothing. Implements [docs/product-requirements.md](../../product-requirements.md).

## 1. Purpose

QuotaBeacon is a local-first Windows tray application that shows remaining AI service
quota for Claude and OpenAI Codex in one place.

The product requirements were written assuming seat-based accounts, where quota is a
percentage of a rolling window. Investigation showed that consumption-based Claude
Enterprise accounts have no such window: usage is billed in currency against an optional
spend limit. QuotaBeacon targets **both models from the start**.

## 2. The central problem

A seat-based account and a consumption-based account report fundamentally different
shapes:

| Account style | What the provider reports | Has a denominator? |
| --- | --- | --- |
| Seat-based (Pro, Max, seat Enterprise) | 5-hour and weekly windows, percent used, reset time | Yes |
| Consumption Enterprise, spend limit set | Currency used, limit, billing period | Yes, derived |
| Consumption Enterprise, no spend limit | Currency used, billing period | **No** |

The third row is the constraint that drives the design. Without a denominator there is no
percentage, no gauge, and no meaningful "20% remaining" alert. Treating it as `0%` or
`100%` would display a confident lie.

## 3. Core abstraction: Meter

A provider returns one `QuotaSnapshot` containing zero or more `Meter` values. A `Meter`
is one independently resetting or accumulating quantity.

```
QuotaSnapshot
  Provider      ProviderId          Claude | Codex
  FetchedAt     DateTimeOffset
  Meters        IReadOnlyList<Meter>
  Error         ProviderError?      null on success

Meter
  Id            string              stable key, e.g. "claude.session5h"
  Label         string              display name
  Kind          MeterKind           Window | Spend
  Ratio         double?             0.0-1.0 consumed; null when unknowable
  ResetsAt      DateTimeOffset?     Window only
  Amount        Money?              Spend only: consumed
  Limit         Money?              Spend only: cap, may be null
  PeriodStart   DateTimeOffset?     Spend only
  PeriodEnd     DateTimeOffset?     Spend only
```

`Ratio` is the single field the UI and alert engine branch on. It is:

- read directly for `Window` meters,
- computed as `Amount / Limit` for `Spend` meters that have a limit,
- `null` for `Spend` meters without a limit.

`Ratio` expresses **consumed** fraction, not remaining. Remaining is presentation
(`1 - Ratio`). Storing one direction only removes a whole class of inversion bugs.

### 3.1 Invariants

Enforced in the `Meter` factory methods, verified by tests:

- `Kind == Window` requires non-null `Ratio`. A window with unknown usage is not a
  window; it is an error.
- `Kind == Spend` requires non-null `Amount`.
- `Ratio`, when present, is clamped to `[0.0, 1.0]`. Providers have been observed to
  report values slightly above 1.0 after a limit is exceeded; clamping keeps the UI sane
  while `Amount`/`Limit` retain the true overage.
- `Limit` and `Amount` must share a currency. Mixed currency is a parse error.

## 4. Component boundaries

C# on .NET 9, WPF for the popup, published as a self-contained single-file executable.

```
QuotaBeacon.Core          no UI, no HTTP, no filesystem. Pure logic + contracts.
  Meter, QuotaSnapshot, Money, ProviderError, MeterKind
  IQuotaProvider
  AlertEngine             ratio and absolute threshold evaluation, de-duplication
  TrayStateResolver       collapses N meters into one icon state
  SnapshotCache           last-known-good with staleness

QuotaBeacon.Providers     authentication, HTTP, response mapping
  ClaudeProvider, CodexProvider
  IAuthSource             one way to authenticate; providers hold an ordered chain
  CliAuthSource           reads local CLI credential files, read-only
  WebAuthSource           uses QuotaBeacon's own embedded browser session
  QuotaMapper             tolerant JSON to Meter projection

QuotaBeacon.App           WPF popup + WinForms NotifyIcon for the tray
  TrayHost                icon lifecycle, menu, click handling
  IconRenderer            runtime-drawn DPI-aware icon
  PopupWindow             the quota card
  SettingsWindow
  WebSignInWindow         embedded WebView2 sign-in, per-provider profile
  RefreshScheduler        refresh loop, backoff
  AppSettings             JSON under %LOCALAPPDATA%

QuotaBeacon.Tests         xUnit; covers Core and Providers mapping via fixtures
  Fixtures/               hand-authored JSON response samples
```

`Core` depends on nothing. `Providers` depends on `Core`. `App` depends on both. Tests
depend on `Core` and `Providers` and never touch the network.

WPF carries the visual work because the popup needs gradients, shadows, eased animation,
and system theme following, none of which WinForms does well. `NotifyIcon` comes from
WinForms purely because WPF has no tray primitive; it is used for the icon and menu only,
via `<UseWindowsForms>` alongside `<UseWPF>`.

The only package dependency is `Microsoft.Web.WebView2`, needed for the embedded sign-in in
section 5.1. It is a Microsoft first-party component, and on Windows 11 its runtime is already
present. No third-party UI library is taken, keeping the review surface small.

## 5. Provider behavior

### 5.1 Authentication sources form an ordered chain

A user who never installed Claude Code or the Codex CLI has no credential file, so CLI-only
authentication would leave them with an empty popup. Each provider therefore walks an ordered
list of `IAuthSource` implementations and uses the first that produces quota data:

1. `CliAuthSource` — reads `%USERPROFILE%\.claude\.credentials.json` (honoring
   `CLAUDE_CONFIG_DIR`) or `%USERPROFILE%\.codex\auth.json` (honoring `CODEX_HOME`).
2. `WebAuthSource` — uses the session in QuotaBeacon's own embedded browser profile, if the
   user has signed in there.

`AuthenticationMissing` is returned only when every source in the chain is unavailable, and its
message names the two ways forward: sign in to the CLI, or sign in through the app.

### 5.2 CLI credentials are read-only and never refreshed

QuotaBeacon opens credential files read-only and never writes them.

**It never performs a token refresh.** Both files hold a refresh token shared with the vendor
CLI; refreshing from a second process races the CLI and can invalidate the user's session. On
expiry the CLI source reports `AuthenticationExpired`, the chain falls through to the web
source, and if that is also unavailable the UI offers both remedies. This is a deliberate
capability sacrifice for correctness.

### 5.3 Capability probing

The exact response shape of consumption-based Enterprise usage endpoints is not publicly
documented and could not be verified against an Enterprise account during design. Each
provider therefore declares an ordered list of candidate sources and probes them:

1. Attempt each candidate in order.
2. First candidate producing at least one valid `Meter` wins and is remembered for the
   session.
3. If a candidate returns HTTP 200 with a body that maps to zero meters, that is
   `ProviderError.UnrecognizedResponse` carrying only the shape (top-level key names and
   JSON types), never values.

This makes an unverified endpoint a runtime discovery instead of a design assumption, and
degrades to an actionable error rather than a wrong number.

### 5.4 Mapping is additive and tolerant

Mapping never requires a field it does not use. Unknown keys are ignored. A response
containing both window and spend information yields meters of both kinds; the UI already
renders any mix. Field-name variants observed across providers (`primary_window`,
`secondary_window`, `five_hour_limit`, `weekly_limit`) are handled as aliases of the same
meter ids.

### 5.5 Independence

Each provider runs on its own task with its own timeout and its own cache entry. A
provider failure marks only that provider stale. The refresh loop awaits both and never
lets one cancel the other.

## 6. Alert engine

Two rule types, chosen by whether `Ratio` exists.

| Meter | Rule | Default |
| --- | --- | --- |
| `Ratio != null` | fire when remaining crosses below a percentage | warn 20%, critical 10% |
| `Ratio == null` | fire when `Amount` rises above an absolute value | **disabled** |

Absolute thresholds have no default because no defensible default exists: the app cannot
know whether a given monthly spend is normal for this organization. The settings screen
surfaces the meter with its current amount so the user can choose a number informed by
what they actually see.

De-duplication keys on `(meterId, level, windowIdentity)` where `windowIdentity` is
`ResetsAt` for windows and `PeriodEnd` for spend. When the window rolls over the identity
changes and the meter becomes eligible to alert again. Crossing back above a threshold
clears the latch for that level, so a meter that recovers can warn again in the same
window.

## 7. Tray icon state

One icon must summarize every enabled meter. `TrayStateResolver` picks a single
representative:

1. Consider only meters eligible to express danger: any `Ratio != null` meter, plus
   `Ratio == null` meters that have a user-configured absolute threshold.
2. Rank eligible meters by severity (critical > warning > normal), then by least
   remaining.
3. The winner supplies the icon color and the tooltip's first line.
4. If no meter is eligible, the icon is neutral and the tooltip reports the raw amounts.
5. If every enabled provider is errored or stale, the icon shows the stale state
   regardless of the last known values.

Rule 4 is what keeps a no-limit consumption account honest: a neutral icon with real
numbers in the popup, rather than a fabricated gauge.

## 8. Freshness and errors

`SnapshotCache` retains the last successful snapshot per provider and exposes
`IsStale` once age exceeds twice the refresh interval. Stale values remain visible but
are visually marked and accompanied by the failure reason and the timestamp of the last
success.

`ProviderError` categories, each mapped to distinct user-facing guidance:

| Category | Meaning | Retry |
| --- | --- | --- |
| `AuthenticationMissing` | no credential file found | no, needs user action |
| `AuthenticationExpired` | credential present but rejected | no, needs CLI re-login |
| `RateLimited` | provider throttled the request | yes, honor `Retry-After` |
| `Network` | connectivity or timeout | yes, exponential backoff |
| `UnrecognizedResponse` | 200 but unmappable | no, needs a code change |
| `Unexpected` | anything else | yes, bounded |

Backoff is exponential from the refresh interval to a 60-minute ceiling, reset on
success. `RateLimited` and `Network` retry; the rest wait for the next manual refresh or
a credential change detected via file watch.

## 9. Security

- No telemetry, no crash reporting, no outbound traffic other than provider endpoints.
- Credential material is never logged. Log redaction is applied at the sink, so a new
  log call site cannot leak by omission.
- `UnrecognizedResponse` diagnostics carry key names and JSON types only, never values.
- Settings persist under `%LOCALAPPDATA%\QuotaBeacon`. Any future secret uses DPAPI
  `CurrentUser` scope. The first release stores no secrets of its own.
- Credential files are opened read-only with no write share request.

### 9.1 The web fallback signs in rather than harvesting

Reading the cookie store of an installed browser is **prohibited**. On Windows it requires
decrypting a DPAPI-protected store, which is the technique used by credential-stealing
malware, is likely to be flagged by endpoint protection, and means handling credentials the
user never presented to this application.

The supported fallback inverts this: the user signs in through an embedded WebView2 window
that QuotaBeacon owns, completing SSO and MFA normally. Cookies are written to a
QuotaBeacon-owned user-data folder under `%LOCALAPPDATA%\QuotaBeacon\WebView2\<provider>`,
isolated per provider and never shared with a system browser profile. Signing out deletes
that folder.

This keeps requirement 6's no-policy-bypass commitment intact: conditional access, SSO, and
MFA all apply to the embedded sign-in exactly as they would in a browser, because it *is* a
browser.

## 10. UI

Visual quality is an explicit product requirement, not a finishing touch. The popup is
the entire product surface: it is seen many times a day for two seconds at a time, so it
has to read instantly and feel native to Windows 11.

### 10.1 Structure

The popup follows the approved mockup: header with wordmark and settings affordance,
three tabs (All, Claude, Codex), content, and a footer carrying last-refresh time and a
manual refresh action. Tabs for disabled providers are hidden; if exactly one provider is
enabled the popup opens on that provider's detail tab.

It is a borderless 430px-wide card anchored above the notification area, positioned
against the work area so it never covers the taskbar or spills off-screen on secondary or
scaled displays. It closes on deactivate and on `Esc`.

### 10.2 Visual language

- **Backdrop.** Win11 Mica via `DwmSetWindowAttribute` (`DWMWA_SYSTEMBACKDROP_TYPE`), with
  rounded corners via `DWMWA_WINDOW_CORNER_PREFERENCE`. On Win10, where those attributes
  are unsupported, it degrades to a solid themed surface with a 1px border. Detected by
  capability probe, never by OS version string.
- **Theme.** Light and dark are both first-class, read from
  `AppsUseLightTheme` and followed live via a registry watch, plus the system accent color
  for the neutral meter fill. No hardcoded background colors.
- **Type.** Segoe UI Variable — Display for the large percentage, Text for body. Numbers
  use tabular figures so a changing value does not shift layout. The hero number is the
  single largest element on screen; labels are muted and one step smaller.
- **Color.** Severity is carried by fill color on a three-stop scale (normal, warning,
  critical) derived from the theme so it holds contrast in both modes. Severity is never
  encoded by color alone: the row also carries a text label, which keeps it readable for
  color-vision deficiency.
- **Motion.** Gauge fills animate to their new value with a 420ms cubic ease-out; the
  hero number counts to its target over the same curve. Tab changes cross-fade with a 6px
  vertical slide over 180ms. Motion is skipped entirely when the system reports reduced
  motion, and on first paint so opening never feels laggy.
- **Depth.** One soft ambient shadow on the card, no shadows on inner elements. Inner
  separation is done with 1px hairlines at low opacity.

### 10.3 Rendering by meter kind

- `Ratio != null`: hero percentage, animated gauge, and either a live reset countdown or
  the billing period end.
- `Ratio == null`: the amount as hero text, billing period beneath, and **no gauge** —
  the gauge track is not rendered at all rather than drawn empty. A quiet hint explains
  that no spend limit is configured, so the absence reads as information rather than as a
  loading state.

This distinction is the visual expression of the section 2 constraint, and it is the one
place where the design must resist the temptation to look uniform.

### 10.4 Stale and error states

Stale values stay visible at reduced opacity with a small badge on the footer giving the
last success time and the reason. Errors render inside the affected provider's row, never
as a dialog, and never replace a value that is still meaningful.

A provider in `AuthenticationMissing` replaces its gauge with a single actionable line and a
sign-in affordance that opens the embedded sign-in window, so a user who has no CLI installed
reaches a working state without leaving the popup.

### 10.5 Tray icon

Drawn at runtime from the resolved tray state so it is crisp at every DPI and reflects
severity without shipping a bitmap per state. It renders a ring whose sweep is the
representative meter's remaining fraction, filled with that meter's severity color; the
neutral state (no eligible meter) draws the ring unfilled. The tooltip's first line is the
representative meter, followed by one line per enabled provider.

## 11. Testing

`Core` is pure and covered directly: meter invariants and clamping, ratio derivation,
alert latching across window rollover and recovery, tray resolution precedence including
the no-eligible-meter path, and cache staleness transitions.

`Providers` mapping is covered by JSON fixtures under `tests/fixtures/` representing a
seat-based response, a spend response with a limit, a spend response without a limit, a
response mixing both, and an unmappable response. Fixtures are hand-authored and contain
no real account data or tokens.

Networking and the WinForms host are not unit tested. `IQuotaProvider` is the seam;
`Core` tests substitute fakes.

## 12. Packaging

`dotnet publish -r win-x64 --self-contained` producing a single-file executable, so the
app runs without an installer or administrator rights. Startup registration is an opt-in
per-user `Run` registry entry, written only on explicit user action.

License: MIT, matching the surrounding ecosystem.

## 13. Deliberately out of scope

Organization-wide dashboards, administrator analytics, general ChatGPT chat message
limits (OpenAI does not expose remaining counts, so any figure would be an estimate),
macOS and Linux, cloud sync, and providers beyond Claude and Codex. The provider boundary
admits new providers later without new abstractions.
