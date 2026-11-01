# ClipVault

A clipboard history for Windows 11 that looks and works like the built-in **Win+V** panel, without its limits.

- **No 4 MB limit.** Full-screen and multi-monitor screenshots are kept.
- **Large history** (500 items by default, adjustable) that survives restarts.
- **Text, images, files and folders.** Ctrl+C in Explorer is saved too, and pasting it later copies the files again.
- **Pin and tag.** Pin items so they're never removed. Create your own tags (e.g. `CMDS`) and pin items into them, and each tag gets its own tab.
- **Search** and **All / Text / Images / Files** tabs.
- **Emoji picker** with full-color Windows emoji, search and recently used.
- **Card actions.** Click **⋯** on a card to slide out:
  - **Ask Claude**: sends the item to your latest Claude Code conversation (`claude --continue "<item>"`)
  - **Paste as plain text** (text), **Save as…** (images), **Open location** (files)
  - **Pin** / **Pin to tag**, and **Delete**
- **Clear all** removes everything except pinned items.
- Follows Windows **light/dark mode** and **accent color**, with the Windows 11 acrylic backdrop.
- **Private.** No network access, no telemetry. Everything stays in `%LOCALAPPDATA%\ClipVault`.

## Usage

| Key | Action |
| --- | --- |
| **Win+V** | Open the history (next to your text cursor) |
| **Enter** / click | Paste the selected item |
| **Shift+Enter** | Paste as plain text |
| **↑ ↓** | Move through items |
| Type anything | Search |
| **Delete** | Delete the selected item |
| **Menu key** | Show the actions for the selected item |
| **Esc** / click outside | Close |

Right-click the tray icon for **Pause saving**, **Settings** and **Exit**.

### How Win+V is taken over

ClipVault catches Win+V with a keyboard hook while it runs, the same technique Microsoft PowerToys uses.
**No Windows settings or registry keys are changed for this.** When ClipVault exits, Win+V opens the Windows panel again.
The only registry entry ClipVault writes is the optional *Start with Windows* entry in `HKCU\...\Run`.

### Privacy

ClipVault skips clipboard content that apps mark as private (password managers use
`ExcludeClipboardContentFromMonitorProcessing` / `CanIncludeInClipboardHistory = 0`), just like Windows does.

## Build

No SDK needed. It uses the C# compiler and .NET Framework 4.8 that ship with Windows 10/11:

```powershell
powershell -ExecutionPolicy Bypass -File build.ps1
.\bin\ClipVault.exe
```

## Project layout

| File | What it does |
| --- | --- |
| `src/App.cs` | Startup, single instance, tray icon, settings window |
| `src/ClipboardService.cs` | Watches the clipboard, reads/writes text, images and files |
| `src/HotkeyHook.cs` | Win+V keyboard hook |
| `src/Store.cs` | History on disk (`index.json` + one file per image) |
| `src/PopupWindow.cs` | The popup: list, tabs, actions, emoji, paste |
| `src/Emoji.cs` | Color emoji rendering through Direct2D/DirectWrite |
| `src/Theme.cs` | Light/dark, accent color, icons, Ask Claude launcher |
| `src/*.xaml` | Styles and layout |

## License

MIT
