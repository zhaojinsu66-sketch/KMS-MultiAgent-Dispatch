# KMS多控 (KMS Multi-Control)

[简体中文](README.md) · [English](README.en.md)

> Formerly “KMS · Multi-Agent Dispatch”.

Run multiple copies of desktop AI agents — **Claude**, **Codex**, **Gemini**, **DeepSeek**, **Grok**, and friends — side by side. Every window signs in with its own account, and you switch, arrange and sign in from one place.

A single ~450 KB Windows executable. Nothing to install, nothing downloaded at runtime, and it builds with the C# compiler that ships with Windows (.NET Framework 4.8).

> The current version only ships the “Account Dispatch” feature. Source for the earlier “Commander / CLI accounts / VMs” modules is still in the tree but no longer wired up; it will be removed in a later release.

## Supported desktop apps

| App | Status |
|---|---|
| Claude Desktop | ✅ Native support (multi-instance, docked placement, in-window sign-in, sign out / switch account) |
| Codex Desktop | ✅ Native support (same, plus a per-window `CODEX_HOME`) |
| Gemini / DeepSeek / Grok Desktop | 🚧 The same approach applies, but the “recognise this app” layer (executable and package name) needs one mapping per app. Not done yet. |

> In other words: multi-instance launching, docking, isolated sign-in and the per-window browser are generic; what is missing is a per-app identification entry. PRs welcome — or wait for “pick your own .exe” support.

## What problem it solves

The official desktop apps only accept one signed-in account at a time. Want to use a work account and a personal one together? You end up signing out and back in, and the sessions kick each other out.

KMS gives **every window its own config directory** (`--user-data-dir`, plus `CODEX_HOME` for Codex), so each window is an independent copy of the official app signed in as its own account. They run side by side without interfering.

## Features

- **Multi-instance**: `+ Add` opens another Claude / Codex window, each with its own data directory
- **Docked into one UI**: click an entry in the sidebar and that window is moved onto the pane's screen position, following KMS as it moves, resizes and minimises. It stays a **normal standalone window**, so typing, sign-in dialogs and file pickers behave exactly as usual
- **In-window sign-in**: every window gets its own “vacuum browser” (Edge with a private data directory — fully separate from your everyday browser, but it remembers this window's own logins, and you can reopen it at any time after closing it). When the app tries to open a web page, KMS catches it and opens it in that window's own browser; once the login callback arrives, the app comes back
- **Sign out / switch account**: the login data is renamed and kept aside (never deleted), then a fresh signed-out window opens
- **Rename, release to desktop, tile all, delete**
- **Single instance**: launching KMS twice just brings the existing window forward instead of running two copies that overwrite each other's config
- **Read-only by design**: everything happens inside the official app's own window; KMS never stores or forwards passwords or tokens

## Screenshot

![Main window](docs/images/main.png)

## Requirements

- Windows 10 1809+ / Windows 11
- .NET Framework 4.8 (ships with Windows, no SDK needed)
- Claude Desktop / Codex Desktop (MSIX builds)

## Build from source

```powershell
powershell -ExecutionPolicy Bypass -File build.ps1
```

The result is `AgentCommander.exe` in the repo root. To run the model-free self-test:

```powershell
.\AgentCommander.exe --selftest selftest.log
```

## How it works

1. **Multi-instance**: launch the official app with `--user-data-dir=<data\real-apps\<window id>>`; for Codex, point `CODEX_HOME` at `codex-home` inside that directory too.
2. **Docking**: the app window is left untouched (no `SetParent`, no style changes). KMS only `SetWindowPos`-es it onto the pane's screen rect and makes KMS its owner window — so it stays above KMS, follows moves/resizes, and is released back to the desktop when KMS goes to the tray. Keyboard, dialogs and browsers all take the native path; no focus stealing needed.
3. **Login redirects**: it watches the browser processes the app spawns (`ProcWatch`, a job object). Some apps open the browser through the Windows shell, which is outside the job — so after you click “Sign in”, KMS also watches new browser processes machine-wide for a while and re-routes login URLs to that window's own browser.
4. **Persistent browser**: each window's browser uses its own `--user-data-dir` (no `--guest`), so closing and reopening it keeps the same session; clicking again while it is already open just brings it to the front.
5. **Data directory**: defaults to `%LOCALAPPDATA%\KMS\data` so upgrading or moving the app never loses logins. A `data` folder or a `datadir.txt` next to the exe takes precedence (portable mode / reusing old data).
6. **No lost windows**: window handles are recorded in `data\embedded.txt`; on the next start, orphans left behind by a force-kill are restored to normal windows.

## Known limitations

- Docking moves someone else's window onto the pane's screen rect; it is not an officially supported approach, so an app update or an unusual DPI/multi-monitor setup may need an adjustment.
- Login redirects rely on the app "opening a URL in a browser". Apps that go through the Windows shell (no new process) can't be intercepted and will land in your default browser — in that case use email sign-in, or switch accounts in your default browser before authorising.
- The **X button only hides KMS to the tray**. Quit from the tray icon; don't force-kill it (a force-kill may leave orphaned windows — the next launch repairs them).
- Several windows editing the same files in the same project folder will overwrite each other.
- Whether stacking multiple subscription accounts violates any terms of service is your call.

## Disclaimer

This project only manages windows and forwards login flows. It does not modify or crack the official apps, and it never stores passwords or tokens. Check that your usage complies with the terms of service you are bound by.

## License

MIT
