# QuotaBeacon App Completion Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Turn the existing Core, Providers, and unfinished WPF UI into a runnable Windows tray application with reliable CLI-to-web authentication fallback, settings, previews, and verified packaging.

**Architecture:** Keep pure quota behavior in `QuotaBeacon.Core`, move authentication failover orchestration into `QuotaBeacon.Providers`, and use `QuotaBeacon.App` as the composition root and Windows lifecycle owner. WPF owns windows and dispatching; WinForms is isolated to `TrayHost`; WebView2 owns app-specific browser profiles.

**Tech Stack:** .NET 9, C# 13, WPF, WinForms `NotifyIcon`, WebView2, xUnit.

## Global Constraints

- Windows-only local-first tray app; no server or telemetry.
- Claude and Codex operate independently and may be enabled separately.
- CLI credentials are read-only and preferred; web sign-in is the fallback.
- Default refresh is 5 minutes; warning is 20% remaining and critical is 10% remaining.
- Credential values and response bodies must never be logged.
- Installed Edge/Chrome cookie stores must never be read.
- Existing user changes in the dirty worktree must be preserved.

---

### Task 1: Restore WPF application compilation

**Files:**
- Modify: `src/QuotaBeacon.App/QuotaBeacon.App.csproj`
- Modify: `src/QuotaBeacon.App/App.xaml.cs`
- Modify: `src/QuotaBeacon.App/Controls/Gauge.cs`
- Modify: `src/QuotaBeacon.App/Theming/Theme.cs`
- Modify: `src/QuotaBeacon.App/Theming/ThemeResources.cs`
- Modify: `src/QuotaBeacon.App/Views/PopupWindow.xaml.cs`

**Interfaces:**
- Consumes: existing WPF and WinForms types.
- Produces: an App project that compiles without ambiguous framework types.

- [ ] **Step 1: Capture the failing build**

Run: `dotnet build src/QuotaBeacon.App/QuotaBeacon.App.csproj --no-restore`

Expected: FAIL with CS0104 ambiguity errors for WPF/WinForms types.

- [ ] **Step 2: Isolate WinForms implicit imports**

Set `<ImplicitUsings>disable</ImplicitUsings>` in the App project and add only the explicit namespaces each file requires. Keep the `Forms` alias inside `TrayHost` so WinForms cannot leak into WPF files.

- [ ] **Step 3: Verify compilation advances**

Run: `dotnet build src/QuotaBeacon.App/QuotaBeacon.App.csproj --no-restore`

Expected: previous CS0104 errors disappear; address any newly exposed compiler errors one at a time.

### Task 2: Make authentication fallback depend on request success

**Files:**
- Modify: `src/QuotaBeacon.Providers/Authentication.cs`
- Modify: `src/QuotaBeacon.Providers/HttpQuotaProvider.cs`
- Modify: `src/QuotaBeacon.Providers/ClaudeProvider.cs`
- Modify: `src/QuotaBeacon.Providers/CodexProvider.cs`
- Test: `tests/QuotaBeacon.Tests/HttpQuotaProviderTests.cs`
- Test: `tests/QuotaBeacon.Tests/AuthenticationTests.cs`

**Interfaces:**
- Produces: `AuthChain.AcquireAvailableAsync(CancellationToken)` returning credentials in priority order without exposing secrets.
- Consumes: `IAuthSource.TryAcquireAsync` and `QuotaSource` endpoint definitions.

- [ ] **Step 1: Add regression tests**

Add tests proving that a rejected CLI credential falls through to a web credential, a successful CLI credential prevents web acquisition, and a Claude web cookie is tried only against the Claude web endpoint.

- [ ] **Step 2: Verify RED**

Run: `dotnet test tests/QuotaBeacon.Tests/QuotaBeacon.Tests.csproj --filter "Authentication|FallsBack"`

Expected: FAIL because the current chain returns only the first acquired credential.

- [ ] **Step 3: Implement credential-aware probing**

Enumerate available credentials in source priority order. Probe only endpoints compatible with each credential kind. On 401/403, continue to the next credential; on success, remember the credential kind and source name as the preferred pair for the session.

- [ ] **Step 4: Verify GREEN**

Run the filtered tests, then all provider tests.

### Task 3: Add a testable Windows application coordinator

**Files:**
- Create: `src/QuotaBeacon.App/Services/AppCoordinator.cs`
- Create: `src/QuotaBeacon.App/Services/ProviderFactory.cs`
- Modify: `src/QuotaBeacon.App/App.xaml.cs`
- Modify: `src/QuotaBeacon.App/Services/RefreshScheduler.cs`
- Test: `tests/QuotaBeacon.Tests/RefreshSchedulerTests.cs`

**Interfaces:**
- Produces: `AppCoordinator.StartAsync`, `RefreshNowAsync`, and `Dispose`.
- Consumes: `AppSettings`, `IQuotaProvider`, `RefreshScheduler`, `TrayHost`, `PopupWindow`, and WPF `Dispatcher`.

- [ ] **Step 1: Add scheduler regression tests**

Cover independent provider failure, forced refresh ignoring backoff, and event delivery containing both provider states.

- [ ] **Step 2: Verify RED for missing coordinator seams**

Run the new tests and confirm failure for the missing integration API.

- [ ] **Step 3: Implement composition and lifecycle**

Load settings, create one shared `HttpClient`, create enabled providers, construct alert/scheduler/UI services, marshal refresh results through `Application.Dispatcher`, show notifications, and dispose every owned service on explicit exit.

- [ ] **Step 4: Implement preview startup**

Parse `--preview seat|spend|mixed`, show `SampleData` without starting HTTP polling, and keep the preview window open for inspection.

- [ ] **Step 5: Verify build and scheduler tests**

Run App build and the new scheduler tests.

### Task 4: Complete sign-in semantics and session revocation

**Files:**
- Modify: `src/QuotaBeacon.App/Views/WebSignInWindow.xaml.cs`
- Modify: `src/QuotaBeacon.App/Services/WebViewSessionStore.cs`
- Modify: `src/QuotaBeacon.Providers/WebAuthSources.cs`

**Interfaces:**
- Produces: sign-in completion based on an authenticated provider request, and `TrySignOut` returning a visible success/failure result.
- Consumes: provider session endpoint validation and provider-owned WebView2 profile paths.

- [ ] **Step 1: Extract testable session validation**

Represent login completion as validation of a known authenticated endpoint rather than the presence of any cookie.

- [ ] **Step 2: Preserve SSO navigation in one WebView profile**

Allow HTTPS navigation required for the active sign-in flow while presenting the destination host in the window; continue blocking unsafe schemes and arbitrary popup behavior.

- [ ] **Step 3: Make sign-out observable**

Return failure when the profile directory cannot be removed, allowing Settings to report that revocation must be retried.

- [ ] **Step 4: Verify with build and manual sign-in smoke path**

Build the App and verify the sign-in window can remain within the app-owned profile across redirects.

### Task 5: Implement Settings window

**Files:**
- Create: `src/QuotaBeacon.App/Views/SettingsWindow.xaml`
- Create: `src/QuotaBeacon.App/Views/SettingsWindow.xaml.cs`
- Create: `src/QuotaBeacon.App/Services/StartupRegistration.cs`
- Modify: `src/QuotaBeacon.App/Services/AppSettings.cs`
- Modify: `src/QuotaBeacon.App/Services/AppCoordinator.cs`

**Interfaces:**
- Produces: provider toggles, refresh interval, warning/critical thresholds, web sign-in/out, and per-user startup registration.
- Consumes: immutable `AppSettings` saved to `%LOCALAPPDATA%\QuotaBeacon\settings.json`.

- [ ] **Step 1: Add validation tests for settings values**

Prove refresh interval clamps to 1-120 minutes and critical threshold cannot exceed warning threshold.

- [ ] **Step 2: Verify RED**

Run the settings tests and confirm the cross-field threshold test fails.

- [ ] **Step 3: Implement the settings UI and save flow**

Use native WPF controls, validate before save, apply settings through the coordinator, and require explicit user action for startup registration and web sign-out.

- [ ] **Step 4: Verify GREEN and App build**

Run settings tests and build the full solution.

### Task 6: Fix tray icon ownership

**Files:**
- Modify: `src/QuotaBeacon.App/Services/IconRenderer.cs`
- Modify: `src/QuotaBeacon.App/Services/TrayHost.cs`

**Interfaces:**
- Produces: exactly one owned current `Icon`, disposed on replacement and shutdown.

- [ ] **Step 1: Change ownership at the renderer boundary**

Clone the icon, destroy the temporary HICON immediately, and return the owned clone.

- [ ] **Step 2: Dispose the previous NotifyIcon image**

Swap the current icon and dispose the prior clone after assignment.

- [ ] **Step 3: Run a repeated-render smoke test**

Render and replace several hundred icons in preview/debug mode while observing stable process GDI object count.

### Task 7: Verify previews, packaging, and documentation

**Files:**
- Modify: `README.md`
- Modify: `docs/product-requirements.md`
- Modify: `docs/superpowers/specs/2026-07-30-quota-beacon-design.md`

- [ ] **Step 1: Run full automated verification**

Run `dotnet build QuotaBeacon.sln`, `dotnet test QuotaBeacon.sln`, and `dotnet publish src/QuotaBeacon.App/QuotaBeacon.App.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true`.

- [ ] **Step 2: Render every preview scenario**

Run Seat, Spend, and Mixed previews in light and dark themes; inspect popup bounds, all three tabs, error state, gauge absence for no-limit spend, and 100%/150% DPI.

- [ ] **Step 3: Update user documentation**

Document install/run, authentication behavior, private endpoint limitations, local data locations, and security constraints.

- [ ] **Step 4: Review the final diff before any commit or push**

Confirm no credentials, build outputs, WebView profiles, or unrelated user files are included.
