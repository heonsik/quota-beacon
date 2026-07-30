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
   opened read-only. These are the tokens Claude Code and the Codex CLI already obtained.
2. **Embedded web sign-in.** For people who never installed either CLI. You sign in through a browser
   window QuotaBeacon owns, and the session stays in its own WebView2 profile.

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

Recommended, and what the numbers below were measured at:

```bash
dotnet publish src/QuotaBeacon.App -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true -p:DebugType=none
```

Output lands in `src/QuotaBeacon.App/bin/Release/net9.0-windows/win-x64/publish/`.

| Variant | Result | Size | Needs .NET installed? |
| --- | --- | --- | --- |
| Self-contained, compressed | one `.exe` | **72 MB** | No |
| Self-contained, uncompressed | one `.exe` | 164 MB | No |
| Framework-dependent | `.exe` **plus** `WebView2Loader.dll` and a `runtimes` folder | 2 MB | Yes, .NET 9 Desktop Runtime |

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
