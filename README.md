# QuotaBeacon

A local-first Windows tray application that shows how much Claude and OpenAI Codex quota you have
left, in one place.

It supports both account styles from the start: seat-based accounts that report rolling windows as a
percentage, and consumption-based Enterprise accounts that report currency spent over a billing
period. A consumption account with no spend limit has no denominator, so it is shown as an amount
with no gauge rather than as a fabricated percentage.

## How it reads your usage

QuotaBeacon does not ask you for a password or store a credential of its own. Each provider walks an
ordered authentication chain and uses the first source that works:

1. **CLI credentials.** `%USERPROFILE%\.claude\.credentials.json` and `%USERPROFILE%\.codex\auth.json`,
   opened read-only. These are the tokens the terminal CLIs already obtained.

   **The Claude desktop app does not write this file.** It keeps its own session under
   `%APPDATA%\Claude`, so having the desktop app open never refreshes the CLI credential — and a
   Claude access token lasts only about eight hours. If you work through the desktop app rather
   than a terminal, use the web sign-in below. It is the path built for exactly this case.
2. **Embedded web sign-in.** For people who never installed either CLI, or who use the desktop app.
   You sign in through a browser window QuotaBeacon owns, and the session stays in its own WebView2
   profile — it survives restarts and reboots, so this is a one-time step.

   **This does not work for accounts that sign in with Google.** Google refuses OAuth inside
   embedded browsers, by policy, to stop a window like this one from intercepting credentials.
   QuotaBeacon does not attempt to disguise itself to get around that. If your Claude account uses
   Google sign-in, run `claude` in a terminal instead: it authenticates through your real browser,
   writes the credential file, and QuotaBeacon notices within seconds.

QuotaBeacon **never refreshes** a CLI token. That refresh token is shared with the vendor's CLI, and
refreshing it from a second process can invalidate your session. On expiry it says so and points you
at the two ways to fix it.

It **never reads another browser's cookie store**. Doing that on Windows means decrypting a
DPAPI-protected store, which is the technique credential-stealing malware uses.

> The usage endpoints are undocumented internal APIs, not published ones. They can change without
> notice. QuotaBeacon probes candidate endpoints and, when a response no longer maps, reports that
> plainly instead of showing a wrong number.

## Requirements

- Windows 10 1809 or later, or Windows 11
- The WebView2 runtime, only if you use the embedded web sign-in. It ships with Windows 11 and with
  Microsoft Edge, so it is almost always already present.
- No administrator rights, and no installer

## Deploying

`dotnet publish` produces **a single `QuotaBeacon.exe`**. Copy that one file anywhere and run it —
there is nothing else to place beside it and nothing to install.

A publish profile carries the right settings, so either of these produces it:

- **Visual Studio** — right-click `QuotaBeacon.App` → **Publish** → the `Portable` profile → **Publish**.
  Note this is *Publish*, not Build. Build leaves a folder of DLLs that needs .NET installed.
- **Command line**

  ```bash
  dotnet publish src/QuotaBeacon.App -p:PublishProfile=Portable
  ```

Output lands in `src/QuotaBeacon.App/bin/Publish/QuotaBeacon.exe`.

| Variant | Result | Size | Needs .NET installed? |
| --- | --- | --- | --- |
| Self-contained, compressed | one `.exe` | **72 MB** | No |
| Self-contained, uncompressed | one `.exe` | 164 MB | No |
| Framework-dependent | `.exe` **plus** `WebView2Loader.dll` and a `runtimes` folder | 2 MB | Yes, .NET 9 Desktop Runtime |

### Handing it out

Copy the one `QuotaBeacon.exe` anywhere and run it. Nothing else goes with it.

Two things to expect the first time, neither of which is a fault:

- **SmartScreen.** The executable is unsigned, so Windows shows "Windows에서 PC를 보호했습니다" on first
  run and needs 추가 정보 → 실행. For wider internal distribution, sign it with a code-signing
  certificate; that is the only thing that removes the prompt.
- **Endpoint protection.** A compressed single-file executable unpacks itself on launch, which some
  scanners look at twice. If your estate uses application allow-listing, get the hash approved before
  handing it round.

Settings and the browser profile live under the running user's `%LOCALAPPDATA%`, so nothing is left
behind machine-wide and no elevation is ever required.

The compressed self-contained build is the one to hand out: a single file, no runtime prerequisite,
no administrator rights. It costs a little startup time to decompress, which is invisible for an app
that then sits in the tray.

`IncludeNativeLibrariesForSelfExtract` is what makes it genuinely one file — without it the native
WPF and WebView2 libraries stay beside the executable.

To start with Windows, use the checkbox in settings. It writes a per-user `Run` registry entry and
nothing else.

## Where it keeps things

`%LOCALAPPDATA%\QuotaBeacon\`

- `settings.json` — providers, refresh interval, thresholds, language, window placement. No secrets.
- `WebView2\<provider>\` — the embedded sign-in profile, if you used it. Signing out deletes it.

## Building and running from source

Requires the .NET 9 SDK.

```bash
dotnet test QuotaBeacon.sln
```

Run profiles are defined in `src/QuotaBeacon.App/Properties/launchSettings.json`:

- **QuotaBeacon (tray)** — the real thing. Lives in the notification area; click the icon to open it.
- **Preview - seat / spend / mixed / settings** — renders a surface with representative data and no
  network access. These are for reviewing the interface; the numbers are samples, not your usage.

Preview mode also writes WPF binding failures to `%TEMP%\quotabeacon-binding.log`, because a broken
binding otherwise shows up as a silently blank element.

## Interface

- Click the tray icon for the card; click the pin to keep it on screen as an ordinary window.
- Minimizing a pinned window returns it to the tray. Closing it hides it without quitting, so
  monitoring never stops silently. Quit from the tray menu.
- English and Korean, following the Windows display language unless you choose otherwise. Interface
  text is translated; provider error text stays in English as the diagnostic it is.

## Author

heonsik.lim

## License

MIT
