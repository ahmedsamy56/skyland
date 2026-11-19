# Dynamic Island (WPF)

A Windows 11 "dynamic island": a black pill pinned to the top-centre of the primary monitor that shows the current media session and expands on hover.

## Stack
- C# / WPF, `net8.0-windows10.0.19041.0`
- Windows media session API (`Windows.Media.Control`, SMTC), so it works with Spotify, browsers and any player that appears in the Windows volume flyout
- Project: `DynamicIsland/DynamicIsland.csproj`, in the `NEXUS.slnx` solution next to the unused WinUI 3 `NEXUS` template project

## Behaviour (MVP)
| State | When | Size (px) |
|-------|------|-----------|
| Idle | no media session | 200 x 46: live PC stats, percentage on top and icon below (below) |
| Compact | media present | 230 x 36: artwork, title, equaliser (animates while playing) |
| Expanded | mouse hovers the pill (or click, per settings; or track-change pop-up) | 400 x 132: artwork, title, artist, previous / play-pause / next, progress bar |

- Spring animation on expand, ease-in-out on collapse; collapse is delayed 250 ms after the mouse leaves.
- Primary monitor only. Pure black pill.
- The window is non-activating (never takes focus), hidden from the taskbar and Alt+Tab, and always on top.
- The window is sized for the expanded pill; the area around the pill is fully transparent, so clicks pass through to apps underneath.

## Microsoft Clock sync (Focus sessions and timers)
The island mirrors the Windows Clock app, read-only: you start, pause and stop sessions in Clock, the island shows them.

- **Compact (no media playing):** 250 x 36 pill with a progress ring, countdown, title and session dots (filled = done, outlined = current). Orange = focus, green = break, blue = timer.
- **Expanded:** a compact 310 x 116 Focus page (the media card stays 400 x 132; the island resizes when you switch pages) with a large glowing progress ring holding the countdown, a colored phase chip (FOCUS / BREAK / TIMER), the title ("Session 1 of 3", or the timer name), a subtitle ("Up next: 5 min break", "Ends at 10:44 AM", or "Paused in Clock"), and wide session bars (solid = done, dimmed = current). Amber subtitle text means the values are frozen (Clock minimized or on another page). When media is also playing, two page dots at the top of the card switch between Media and Focus.
- **Media wins the compact pill** when both are active; the Focus page is one click away in the expanded card.
- Toggle: Settings > Integrations > Sync Microsoft Clock (on by default). Off = Clock is never touched.

**How it works and its limits.** Clock has no public API, so `Services/ClockSyncService.cs` reads Clock's on-screen text through UI Automation once a second on a background thread. Verified by probing:
- Needs no admin rights or Windows permission.
- Works while the Clock window is open on the **Focus sessions** or **Timer** page.
- **Minimized: Windows exposes no UI elements at all.** The island then freezes the last values, dims them and shows "Clock minimized". It never guesses.
- If Clock is open on another page (Alarm, Stopwatch, World clock), the island shows the frozen values and "Open Focus in Clock".
- English UI only: the parser reads strings such as `14 minutes 49 seconds, Remaining Of, 15 minutes 0 seconds`. A Clock update or another language can break it; the symptom is the Clock pill never appearing.
- Elements used (UI Automation ids): `FocusButton`, `TimerButton`, `TickTimer`, `TitleTextBlock`, `FocusSessionPlayPauseButton`, `UpcomingBlock`, `TimerViewGrid`, `TimerValueText`, `TimerNameText`, `TimerPlayPauseButton`. Only the countdown, phase and play/pause state are read, never the task list.
- If several timers run, the one that finishes first is shown.
- Not implemented: controlling Clock from the island, and a phase-end pop-up.

## Power pop-up
When the charger is connected the island flashes a 230 x 40 pill for 3 s: green bolt, green "Charging", and the battery percentage. Unplugging shows a neutral "Unplugged" with a battery outline and the percentage. Afterwards the island returns to whatever it was showing (stats or media).

- Driven by `SystemEvents.PowerModeChanged` (`StatusChange`), so there is no polling. The handler compares against the last known AC state, so battery-level changes do not trigger it.
- Takes priority over every other state, including expanded.
- Not shown on PCs without a battery, and not at app start.
- Fixed 3 s duration and always on: there is no Settings option yet.
- State machine: `IslandState.Notice`; `ShowPowerNotice()` in `MainWindow.xaml.cs`. Some laptops report the change a second or two after plugging in.

## PC stats (idle pill)
When no media is playing the pill shows a small icon and a percentage for CPU, memory, GPU and battery, refreshed every 2 s. Icons are 12x12 vector `Path`s in `MainWindow.xaml` (no icon font); hover an icon for its name.

| Item | Source | Notes |
|------|--------|-------|
| CPU | `GetSystemTimes` delta | total load across all cores |
| RAM | `GlobalMemoryStatusEx` | physical memory in use |
| GPU | "GPU Engine" performance counters | busiest engine (like Task Manager); hidden if Windows exposes no counters. Works for NVIDIA, AMD and Intel, no vendor tools |
| Battery | `GetSystemPowerStatus` | hidden on PCs without a battery; a bolt appears on the icon when plugged in |

- A value turns red at 85% or more (battery at 20% or less while unplugged).
- Sampling runs only while the idle pill is visible, so it costs nothing while media is playing. The first reading appears about 0.6 s after the pill shows, because CPU and GPU are deltas.
- Temperature is not included: CPU temperature needs LibreHardwareMonitor and usually administrator rights. Deferred on purpose.
- Requires the `System.Diagnostics.PerformanceCounter` NuGet package (for the GPU counters only).

## Settings window
Open it from the tray (double-click, or right-click > Settings...). Changes apply immediately; there is no Save button.

| Section | Setting | Default | Effect |
|---------|---------|---------|--------|
| General | Start with Windows | off | Adds/removes the Run registry value |
| Island | Expand when you: Hover / Click | Hover | Hover expands on mouse enter; Click expands on click of the compact pill. Both collapse 250 ms after the mouse leaves |
| Island | Pop up on track change | off | Expands for 3 s when the track changes (not on app start, pause or resume) |
| Integrations | Sync Microsoft Clock | on | Mirrors Clock's Focus sessions and timers (see above) |

**Adding a setting later:** add a property to `AppSettings`, add a row to a section in `SettingsWindow.xaml` (use the `Card`, `RowTitle`, `RowHint`, `ToggleSwitch` styles), save in its `Click` handler, and read `SettingsService.Current` where the feature runs. For a new group, add a `SectionLabel` plus a `Card`.

## Code map
| File | Role |
|------|------|
| `App.xaml(.cs)` | Startup, single-instance mutex, tray lifetime, shared `IconButton` style |
| `Services/TrayService.cs` | Tray icon (drawn in code, WinForms `NotifyIcon`): double-click or "Settings..." opens Settings, "Exit" quits |
| `Services/StartupService.cs` | Autostart via `HKCU\Software\Microsoft\Windows\CurrentVersion\Run` (no admin needed). The registry is the source of truth, not the settings file |
| `Services/ClockSyncService.cs` | Reads Microsoft Clock's Focus session and timers via UI Automation (read-only) |
| `Services/SystemStatsService.cs` | CPU / RAM / GPU / battery sampling for the idle pill |
| `Services/SettingsService.cs` | `AppSettings` stored as JSON in `%AppData%\DynamicIsland\settings.json`; `SettingsService.Current` is read live by the island |
| `SettingsWindow.xaml(.cs)` | Dark single-page settings window with sections. Toggle and segmented-button styles live in `App.xaml` |
| `MainWindow.xaml(.cs)` | Pill UI, state machine (Idle / Compact / Expanded), animations, hover and button handling |
| `Services/MediaService.cs` | Wraps SMTC: snapshot (title, artist, artwork, play state), timeline, play/pause/next/previous |
| `Native/NativeMethods.cs` | `WS_EX_TOOLWINDOW` and `WS_EX_NOACTIVATE` window styles |
| `app.manifest` | Per-monitor DPI awareness |

## Design decisions
- `MediaService` is the only code that touches WinRT, so future features (volume HUD, notifications) can be added as separate services without changing the UI plumbing.
- Overlapping media events are handled with a version counter, so the latest refresh wins.
- Artwork is only reloaded when title, artist or album changes.
- Progress position is extrapolated from SMTC `LastUpdatedTime`, because players update it only occasionally.

## Run
Development:
```
cd DynamicIsland
dotnet run
```

## Release build (for daily use and Start with Windows)
Self-contained single file (about 78 MB, no .NET install needed), published to a stable folder:
```
cd DynamicIsland
dotnet publish -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true -p:DebugType=none -o D:\Apps\DynamicIsland
```
Run `D:\Apps\DynamicIsland\DynamicIsland.exe`, then turn on **Start with Windows** in Settings from that copy, so the registry entry points at the stable path. Quit the running island (tray, Exit) before republishing, or the exe is locked. Only one instance can run, so a `dotnet run` copy and the published copy cannot both be open.
Quit from the tray icon (right-click, Exit). Launching a second copy exits immediately.

## Known gaps / next ideas
- "Start with Windows" registers the exe path it is currently running from. If that is `bin\Debug`, rebuilding or moving the project breaks autostart. Publish to a stable folder (`dotnet publish -c Release`) and enable it from there.
- Primary monitor only; the position is computed once at startup and does not follow resolution changes.
- Ideas for later: volume/brightness/charging HUD, auto-pop on track change, notification mirroring, settings.
