# Instructions for AI agents

How to build ClipVault from this repo and install it so it **replaces the Windows clipboard panel (Win+V)**.

## Working with other agents

More than one AI agent edits this repo at the same time (currently Claude Code and OpenCode). To avoid stepping on each other:

- **OpenCode works on branches named `opencode/<topic>`**, never directly on `main`. Claude Code keeps working on `main`.
- Before editing, run `git status`. A dirty tree that isn't yours belongs to the other agent: don't stage, revert or overwrite it.
- Only touch files your change needs. Don't reformat or "clean up" a file you didn't otherwise change.
- Commit only your own files, by path (`git add src/Foo.cs`), never `git add -A`.
- Never `git reset --hard`, `git checkout -- .`, rebase or force-push a branch you didn't create.
- Shared files (`AGENTS.md`, `README.md`, `build.ps1`, `src/*.xaml`) are the usual conflict points: keep edits small and mention them in the commit message.
- Each agent builds and installs its own copy; a build can fail while the other agent is mid-edit. Re-read the file before assuming an error is yours.

## Requirements

- Windows 10 or 11 (x64). Windows 11 is needed for the acrylic/Mica look and Fluent icons.
- Nothing to install: the build uses the C# compiler and .NET Framework 4.8 that ship with Windows
  (`%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe`). Do **not** install the .NET SDK, and do not
  convert the project to `dotnet build` or a `.csproj`.
- No admin rights. Everything is per-user.

## Install (replaces Win+V)

From the repo root, in PowerShell:

```powershell
powershell -ExecutionPolicy Bypass -File install.ps1
```

This will:
1. Build `bin\ClipVault.exe` (via `build.ps1`).
2. Stop any running ClipVault.
3. Copy the exe to `%LOCALAPPDATA%\Programs\ClipVault\ClipVault.exe`.
4. Start it. On start, it adds itself to `HKCU\Software\Microsoft\Windows\CurrentVersion\Run` (start with Windows)
   and catches Win+V with a low-level keyboard hook.

**Do not** disable Windows clipboard history, edit `DisabledHotkeys`, or restart Explorer. The hook replaces Win+V without
changing any Windows settings, and Win+V goes back to Windows automatically when ClipVault exits.

### Verify

```powershell
Get-Process ClipVault                                                                      # must be running
(Get-ItemProperty 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run').ClipVault         # autostart entry
Get-Content "$env:LOCALAPPDATA\ClipVault\log.txt" -ErrorAction SilentlyContinue           # should be empty / no errors
```

Then ask the user to press **Win+V**. The ClipVault popup should open next to their text cursor.
Don't simulate Win+V yourself while the user is typing: the popup takes keyboard focus.

## Build only (no install)

```powershell
powershell -ExecutionPolicy Bypass -File build.ps1      # output: bin\ClipVault.exe
```

A build succeeds only when it prints `Built ...\bin\ClipVault.exe`. If `bin\ClipVault.exe` is locked, a copy is running
from `bin`: stop it first (`Stop-Process -Name ClipVault`).

## Uninstall

```powershell
powershell -ExecutionPolicy Bypass -File uninstall.ps1               # removes app and autostart, keeps history
powershell -ExecutionPolicy Bypass -File uninstall.ps1 -RemoveData   # also deletes %LOCALAPPDATA%\ClipVault
```

## Rules when changing the code

- **C# 5 only** (`-langversion:5`): no `$"..."` strings, `?.`, `nameof`, expression-bodied members, `out var`,
  or auto-property initializers.
- Write non-ASCII characters in `.cs` files as `\uXXXX` escapes (icon glyphs, `×`, `…`...).
  XAML files are UTF-8 and use `&#xE718;` style escapes.
- UI is loose XAML (`src/*.xaml`) embedded as resources and loaded with `XamlReader`. There is no code-behind, so
  handlers are attached in C# by `x:Name` or by routed events. Colors come from `DynamicResource` brushes set
  in `Theme.Apply()`; don't hard-code colors.
- Heavy work (image encode, file I/O, thumbnails, emoji rendering) must stay off the UI thread.
- Threads: the Win+V keyboard hook has its own thread (`HotkeyHook`), because Windows routes every keystroke on
  the PC through it. All clipboard reads/writes go through `ClipboardService`'s clipboard thread with a time
  limit (`OnClipboardThread`), because reading the clipboard can block on whichever app copied the data.
  Clipboard access uses the plain Win32 functions (`Win32Clipboard`), not WPF/OLE: data is placed immediately and
  the owner is the listener window on the UI thread, so a slow worker can never block later copies/pastes.
  Never call `System.Windows.Clipboard` (OLE) anywhere.
- After changing anything, rebuild and re-run `install.ps1` so the installed copy is updated.

## Where things are

| Path | Purpose |
| --- | --- |
| `src/App.cs` | `Main`, single instance, tray, settings window, autostart |
| `src/HotkeyHook.cs` | Win+V keyboard hook |
| `src/ClipboardService.cs` | Clipboard capture and paste-back |
| `src/Store.cs` | History storage (`%LOCALAPPDATA%\ClipVault\index.json`, `items\`) |
| `src/PopupWindow.cs` | Popup UI logic |
| `src/Theme.cs` | Theme colors, icons, Ask Claude launcher |
| `src/SyncService.cs` | Manual Push/Pull of pinned text, tags and folders through ClipVault's Firebase project `clipvault-a24db` (Firestore REST + Email/Password; API key built in). Optional end-to-end encryption per account (passphrase -> PBKDF2-SHA256 -> AES-256-CBC + HMAC-SHA256). Newer change wins; tombstones for deletes/unpins. Network calls run off the UI thread. |
| `src/Emoji.cs`, `src/Emojis.txt` | Color emoji rendering and the emoji list |

Command-line switches: `--startup` (quiet start, used by autostart), `--settings` (opens Settings, also in a running copy),
`--portable` (run without installing). Run from anywhere else, `ClipVault.exe` installs itself to
`%LOCALAPPDATA%\Programs\ClipVault` (this is how the GitHub release installs).

## Sync server (Firebase project `clipvault-a24db`)

Firestore documents live under `users/{uid}/items|tags|folders/{id}` as `{ data: bytes, state, updated }` (`data` = 1 flag byte +
gzip JSON, plain = 1 / encrypted = 2) and `users/{uid}/meta/key` (`mode`, `salt`, `iter`, `check`, `updated`).
Firestore rules to publish (Firestore > Rules), which also cap entry size so the project can't be used as free storage:

```
rules_version = '2';
service cloud.firestore {
  match /databases/{database}/documents {
    match /users/{uid}/{coll}/{doc} {
      function owner() { return request.auth != null && request.auth.uid == uid; }
      allow read, delete: if owner();
      allow create, update: if owner()
        && coll in ['items', 'tags', 'folders', 'meta']
        && request.resource.data.keys().hasOnly(['data', 'state', 'updated', 'mode', 'salt', 'iter', 'check'])
        && (!('data' in request.resource.data) || request.resource.data.data.size() < 512000);
    }
  }
}
```