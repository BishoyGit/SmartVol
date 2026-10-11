# SmartVol (C# / WPF port)

Per-application + global volume control with an on-screen overlay.
C# port of `../main.py` — same behavior, professional UI, no Python runtime needed.

Every hotkey is user-configurable (tray → Settings → Hotkeys: click a
shortcut, then press any combination of Ctrl / Shift / Alt / Win plus any
key). The defaults are:

| Keys | Action |
| ---- | ------ |
| Right Ctrl + ↑ | Raise the focused app's volume |
| Right Ctrl + ↓ | Lower the focused app's volume |
| Right Ctrl + → | Raise the system (master) volume |
| Right Ctrl + ← | Lower the system (master) volume |

An overlay shows the app icon, name, window title and volume level
(or "System Volume" for the global control). A tray icon offers output-device
switching, a hotkey repair action, settings and exit. The tray menu and the
settings device list refresh automatically (debounced) when audio devices are
connected, disconnected or changed.

## Run / build

Requirements: Windows 10/11. Framework-dependent runs need the
.NET 10 desktop runtime; the self-contained publish needs nothing.

```powershell
# Debug run
dotnet run --project SmartVol

# Release build
dotnet build SmartVol/SmartVol.csproj -c Release

# Both distributables at once (see table below)
.\publish.ps1
```

| File | Size | Needs |
| ---- | ---- | ----- |
| `publish/SmartVol.exe` | ~2 MB | .NET 10 Desktop Runtime (already on dev PCs) |
| `publish/SmartVol-portable.exe` | ~165 MB | nothing — runs on any Windows 10/11 PC |

Why the difference? The portable exe bundles the entire .NET runtime +
WPF stack (~99% of its size); our actual code is under 200 KB. Trimming
can't shrink it: our tray icon stack (WinForms `NotifyIcon`) is explicitly
blocked from trimming by the .NET SDK, and WPF + trimming is unsafe
(reflection-based XAML bindings would silently break). So: use `SmartVol.exe`
on your own machines, `-portable` everywhere else.

Useful flags: `SmartVol.exe --show-settings` opens settings directly,
`SmartVol.exe --selftest-audio` runs a headless audio-pipeline check
(device list, default-device re-select, net-zero master-volume round trip)
and exits. Both log to `smartvol.log` next to the exe.

## Settings

`settings.json` (next to the exe, mirrored to `%APPDATA%\SmartVol\`)
is compatible with the Python version's file:

```json
{
  "HiddenDevices": ["{0.0.0.00000000}.{...}"],
  "Step": 0.05,
  "OverlayDurationMs": 1300,
  "OverlayOpacity": 0.92,
  "RunAtStartup": false,
  "HotkeyEitherSideModifiers": false,
  "Hotkeys": {
    "AppUp": { "Modifiers": [163], "Key": 38 },
    "AppDown": { "Modifiers": [163], "Key": 40 },
    "SystemUp": { "Modifiers": [163], "Key": 39 },
    "SystemDown": { "Modifiers": [163], "Key": 37 }
  }
}
```

`Modifiers`/`Key` are virtual-key codes (163 = Right Ctrl, 37–40 = arrows).
Only the exact combination fires; everything else passes through untouched.
With `HotkeyEitherSideModifiers` on (Settings → "Either side works"), left
and right variants of Ctrl / Shift / Alt / Win count as the same key, so
e.g. either Ctrl + ↑ works. Files saved before custom hotkeys existed
simply miss the section and keep the classic Right Ctrl + Arrows defaults.

Everything is also editable in the Settings window (tray → Settings):
volume step (1–20%), overlay duration/opacity, run-at-startup, all four
hotkeys, and the per-device tray visibility list (applies instantly).

## Project layout

```text
SmartVol/
  App.xaml(.cs)            startup, single-instance guard, global logging
  VolumeController.cs      hotkey event -> audio + overlay glue
  TrayManager.cs           NotifyIcon, device menu, settings/exit
  Services/
    AudioService.cs        NAudio: sessions, master volume, devices, icons
    PolicyConfig.cs        default-device switching (all 3 roles)
    HotkeyService.cs       WH_KEYBOARD_LL hook for Right Ctrl + Arrows
    ForegroundAppService.cs foreground pid / process / exe / title
    ConfigService.cs       settings.json load/save (+ AppData mirror)
    StartupService.cs      HKCU Run entry
    NativeMethods.cs       user32 P/Invoke
    AppLogger.cs           rolling file log (smartvol.log)
  UI/
    OverlayWindow.xaml(.cs)   modern overlay card, per-monitor placement
    SettingsWindow.xaml(.cs)  dark settings UI
  Assets/app.ico           app + tray icon (see Assets/ATTRIBUTION.txt)
```

## Fixes & improvements vs the Python version

- **No polling loop or watchdogs**: hotkey events go straight to the UI
  dispatcher; the hook is OS-maintained with a tiny stuck-modifier/hook check.
- **Single audio-session enumeration** with expired-session pre-filtering
  (was a double pass that could hit stale COM objects twice).
- **Device switch sets all 3 roles** (Console/Multimedia/Communications),
  not just Console, so it works for games, media and chat apps.
- **Tray refresh swaps only the menu** instead of destroying/recreating the
  whole tray icon (which could drop or duplicate the icon).
- **Overlay never steals focus** (`ShowActivated=false`, tool-window flag)
  and **positions on the monitor owning the foreground window** with DPI
  awareness (was: primary screen only, `lift()` could steal game focus).
- **Icon cache remembers misses**, guarded by a lock; app icons come from
  `ExtractAssociatedIcon` (no manual HICON juggling/leaks).
- **Single-instance mutex**, global exception logging, config clamping,
  run-at-startup option, `--selftest-audio` diagnostics.

## Icon

App icon source: speaker glyph from Flaticon
(`https://www.flaticon.com/free-icon/speaker_149139`, free license with
attribution — see `Assets/ATTRIBUTION.txt`), composed locally onto a
gradient tile and exported as multi-size ICO.
