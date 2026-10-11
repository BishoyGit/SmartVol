# Changelog

## [1.1.1] - 2026-10-11

### Fixed

- Reduced stuttering during per-app volume hotkeys by moving session work off
  the UI thread, coalescing repeated same-direction presses for one process,
  and preferring exact audio-session PID matches.
- Restored per-app overlay icons with background extraction and persistent
  caching under `%LOCALAPPDATA%\SmartVol\IconCache`.
- Kept loaded app icons visible across repeated hotkeys and switches between
  per-app and system volume overlays.
- Retrieved executable paths for games that do not expose `Process.MainModule`,
  allowing their icons to be extracted and cached.

### Release assets

- `SmartVol.exe` — framework-dependent Windows x64 single-file executable;
  requires the .NET 10 Windows Desktop Runtime.
- The self-contained `SmartVol-portable.exe` is intentionally deferred and is
  not included in this release.
