# QuotaBeacon product requirements

Status: Draft

Date: 2026-07-30

## 1. Product summary

QuotaBeacon is a local-first Windows tray application that shows the remaining usage quotas for Claude and OpenAI Codex in one place. It is intended for people who may use only Claude, only Codex, or both services.

The application monitors the currently signed-in user's quota. It does not require organization administrator privileges and does not attempt to show organization-wide usage.

QuotaBeacon supports two account styles from the first release:

- **Seat-based** accounts, which report rolling windows with a percentage and a reset time.
- **Consumption-based** accounts, which report currency spent over a billing period against an optional spend limit.

A consumption-based account with no spend limit has no denominator, and therefore no percentage and no gauge. This case is a first-class state, not an error. See [design section 3](superpowers/specs/2026-07-30-quota-beacon-design.md).

## 2. Goals

- Show Claude and Codex quota status without repeatedly opening their desktop or web applications.
- Support any combination of the two providers without making one provider a dependency of the other.
- Prefer existing Claude Code and Codex CLI authentication.
- Use an approved browser session as a fallback when CLI authentication cannot provide quota data.
- Keep credentials and quota processing on the user's Windows device.
- Notify the user before the remaining quota becomes critically low.

## 3. Non-goals for the first release

- Organization-wide dashboards or administrator analytics
- Prompt, conversation, or source-code collection
- General ChatGPT message, image, voice, or file-upload limits
- macOS or Linux support
- Cloud synchronization or a central relay server
- Providers other than Claude and Codex

## 4. Provider behavior

Claude and Codex are separate provider modules with a shared result contract. A failure in one provider must not prevent the other provider from updating or displaying its last known state.

On first launch, QuotaBeacon detects available Claude Code and Codex CLI authentication. It presents the detected providers and authentication paths before enabling monitoring. The user confirms which providers to enable.

Providers must accept both account styles. A provider reports whichever quota shape its endpoint returns; the account style is discovered at runtime rather than configured.

QuotaBeacon reads CLI credentials read-only and never refreshes them, because the refresh token is shared with the vendor CLI and a second refresher can invalidate the user's session. Expired credentials produce an actionable error directing the user to re-authenticate in the CLI.

Browser-session fallback is **deferred out of the first release**. On Windows it requires decrypting the DPAPI-protected cookie store, which is the technique used by credential-stealing malware, is likely to be flagged by endpoint protection, and contradicts section 6's commitment not to bypass browser security policy. When CLI credentials are insufficient, QuotaBeacon reports the condition with instructions instead. Revisit only with explicit security-owner approval.

Future providers such as Gemini or GitHub Copilot should be addable through the same provider boundary, but extensibility must not add unused first-release features.

## 5. Monitoring and notifications

- Default refresh interval: 5 minutes
- Quotas with a denominator: default warning at 20% remaining, critical at 10% remaining.
- Quotas without a denominator: absolute amount thresholds, **disabled by default**, because no defensible default exists for what a given organization considers high spend. Settings surface the current amount so the user can choose an informed number.
- Refresh interval and thresholds are configurable.
- The application displays the last successful refresh time.
- Stale data must be visibly distinguished from current data.
- Repeated refreshes must not repeatedly emit the same threshold notification.

## 6. Security and privacy

- Quota data and authentication material are processed locally.
- Authentication material must never be written to application logs.
- The application must not include telemetry in the first release.
- Network requests are restricted to the relevant Claude and OpenAI endpoints.
- Stored sensitive values use Windows user-scoped protection.
- Browser-session access is opt-in during initial setup and revocable later.
- The application must not bypass company SSO, MFA, conditional-access, or browser security policies.

## 7. Core user experience

### First launch

1. Detect Claude Code and Codex CLI authentication independently.
2. Show which providers were detected and which authentication path each will use.
3. Let the user enable Claude, Codex, or both.
4. Request one-time permission to use a selected Edge or Chrome profile as fallback.
5. Perform a test refresh and clearly report success or actionable failure per provider.

### Normal operation

- QuotaBeacon starts in the Windows notification area.
- Clicking the tray icon opens a compact quota summary.
- The summary uses three tabs: All, Claude, and Codex.
- The All tab compares both enabled providers. Each provider tab shows the selected provider's detailed quota windows.
- If only one provider is enabled, the unavailable provider tab is hidden and the application opens that provider's detail view by default.
- Each enabled provider shows remaining quota, reset time when available, data freshness, and provider-specific errors.
- Settings allow provider toggles, refresh interval, notification thresholds, browser-session permission, and startup behavior to be changed.

## 8. Reliability requirements

- Preserve the last successful result when a refresh fails, while marking it stale.
- Apply bounded retries and backoff instead of polling aggressively.
- Treat authentication failure, rate limiting, connectivity failure, and response-format change as distinct error categories.
- Parsing failures must not expose response bodies containing sensitive data in logs.

## 9. Success criteria for the first release

- A Claude-only, Codex-only, and dual-provider user can complete setup.
- Displayed values match each provider's official usage screen within the freshness of the source data.
- One provider remains usable when the other is unavailable.
- Default 20% and 10% alerts fire once per quota window and can be customized.
- No credentials appear in logs, crash reports, or network traffic to unrelated domains.

## 10. Decisions

Resolved in the [design document](superpowers/specs/2026-07-30-quota-beacon-design.md):

- UI technology and packaging: C# on .NET 9, WPF popup, WinForms `NotifyIcon` for the tray, self-contained single-file executable, no third-party packages.
- Visual styling: Win11 Mica backdrop with rounded corners, live light/dark following, Segoe UI Variable with tabular figures, animated gauges, severity never encoded by color alone. Visual quality is a requirement, not a finishing touch.
- Quota fields per provider: discovered at runtime through candidate-source probing rather than hardcoded, because consumption-based Enterprise response shapes are undocumented.
- Browser-profile experience: deferred out of the first release for the security reasons in section 4.
- Startup: opt-in per-user `Run` registry entry, written only on explicit user action.
- License: MIT.

Still open:

- Update-distribution policy. The first release is a manually downloaded portable executable; no updater ships with it.
