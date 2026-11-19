# SkyLand (WPF)

SkyLand, by SkyZack: a Windows 11 "dynamic island", a black pill pinned to the top-centre of the primary monitor that shows the current media session and expands on hover. Formerly called Dynamic Island, which is why the code namespaces, project folders and the settings folder (`%AppData%\DynamicIsland`) keep that name.

## License
Apache License 2.0 (`LICENSE`, verbatim from the official text) with a short `NOTICE` file ("Copyright 2026 C B Sajan (https://github.com/cbsajan)", published under the SkyZack name). The license covers the source code only; the SkyLand and SkyZack names, logos and the app icon are not licensed for use in forks (stated in the README).

## Start-up animation
Each time SkyLand starts, the island plays a short intro (about 4 seconds in total): a 12 px dot at the top springs open into the standard 270 x 60 pill, the SkyLand logo (the actual artwork from `skyland-dark.svg`, drawn as WPF paths in `MainWindow.xaml`) pops in with a small overshoot, then "SkyLand" rises in letter by letter, holds, and the pill shrinks into whatever the island normally shows.
- The intro owns the island while it plays; alerts and prompts wait their turn.
- It always plays (there is no setting for it). When Windows animations are off (`SystemParameters.ClientAreaAnimation`) the island goes straight to the standard pill and shows the finished logo and name for 3 s, with no spring, pop or rising letters.
- Code: `MainWindow.Intro.cs` (`PrepareIntro`, `StartIntroAsync`, `PlayIntro`); the state is `IslandState.Intro`.
- It plays on every start, including start with Windows. If that gets tiresome, turn the switch off.

## Branding
- Name: **SkyLand**. Exe: `SkyLand.exe` (`<AssemblyName>` in `DynamicIsland.csproj`). Window, tray and Settings titles say SkyLand; the Settings footer reads "SkyLand 1.0.0 · by SkyZack".
- Icon: `DynamicIsland/Assets/skyland.ico` (16, 24, 32, 48, 64, 128, 256 px), built from the artwork in `skyland-dark.svg` (blue S, white peak) on a `#000000` rounded tile; the 16 to 32 px sizes use larger artwork so they stay readable. Source with the background: `Assets/skyland-icon.svg`. It is used as the exe icon (`ApplicationIcon`), the tray icon and the Settings window icon (embedded as a WPF `Resource`).
- Renaming deliberately changed only what users see. Internal names are unchanged so nothing breaks: namespaces, project folders, the single-instance mutex, the pipe `DynamicIsland.Agent`, `island-hook.exe`, and the settings folder.
- Migration on startup: an autostart entry made under the old name (`DynamicIsland` in the Run key) is moved to `SkyLand` and pointed at the running exe, and the Claude Code hook entries are repointed at this install's `hook\island-hook.exe` if they were connected from another folder.

## Stack
- C# / WPF, `net8.0-windows10.0.19041.0`
- Windows media session API (`Windows.Media.Control`, SMTC), so it works with Spotify, browsers and any player that appears in the Windows volume flyout
- Project: `DynamicIsland/DynamicIsland.csproj`, in the `NEXUS.slnx` solution next to the unused WinUI 3 `NEXUS` template project

## Behaviour: capsule, bubble, card
The island works like the iOS Dynamic Island: **one main thing at a time, plus a small round bubble**.

```
 [performance bubble]  [ main capsule / card ]  [ activity bubble ]
                         [ alert strip ]
```

| State | When | Size (DIPs) |
|-------|------|-------------|
| Idle | nothing is on | **232 x 32**: one row of icon + percentage for CPU, memory, GPU and battery |
| Capsule | music, a Focus session or Claude is on | **44 high**, 240-276 wide (Claude 252, 262 for several sessions, 276 when it needs you; Focus 240; music 252): leading mark, label, trailing value |
| Card | hover the capsule or the bubble (or click, per settings; or a track-change pop-up) | **340 wide**, height by content (Claude 18+28+8+46 per session, music 164, Focus 106, plus 44 for the PC row; System card 150), corner radius 38 |
| Pop-ups | charging / unplugged, Claude needs you or is done, update prompt, start-up intro | **270 x 60** |

- **Priority:** Claude (a working or waiting session) > Focus > music. The first is the **main capsule**, the second is the **bubble** on its right. A third one only shows inside the card. An open but idle Claude session only counts when nothing else is on.
- **Hover the capsule** = its card. **Hover the bubble** = that activity's card (for example the music player with previous / play / next while Claude runs), and the capsule's activity shrinks into the bubble. Hovering the shrunken bubble swaps back. The PC row (CPU, RAM, GPU, battery) shows only on the main card, not on the bubble's card. Hovering the idle island opens the **System card** with four ring gauges.
- **Capsules:** Claude = status ring around the Claude symbol (spinning violet arc = working, breathing amber ring = needs you), project name, timer (or the tool name when it needs permission); several sessions = a stack of avatars, "N sessions", "N needs you" / "all working". Focus = ring with the minutes, "Focus / session 1 of 3", countdown. Music = artwork, title and artist, equaliser.
- **Bubble:** Claude symbol in its status ring with a count badge when there are several sessions; music artwork inside a progress ring; Focus ring with the minutes.
- **Performance bubble (left, optional, off by default):** Settings > Island > Performance ring. *Rings* = four concentric rings (CPU outside, then memory, GPU, battery); *CPU* = one ring with the percentage. A ring turns red at 85% (battery below 20% while unplugged). It shows only while a capsule is on.
- **Alert strip:** a short line under the capsule: "High CPU / Memory / GPU load 92%" with all three readings, or "Battery low 14% / Plug in the charger". Triggers when CPU, memory or GPU stays at 85% or more for two readings in a row (so a spike never alerts), or the battery is below 20% and unplugged. Each kind shows for 5 s, then stays quiet for 2 minutes. Toggle: Settings > Island > Heavy load and low battery alerts. Sampling runs every 5 s in the background (2 s while the numbers are visible). Test seam: environment variable `SKYLAND_HOT_LEVEL` replaces the 85% threshold.
- Code: `MainWindow.xaml.cs` (`ApplyState`, `SizeOf`, bubbles), `MainWindow.Stats.cs` (stats, gauges, perf bubble, alerts). The GPU reading tolerates up to four empty samples in a row before it hides, because the Windows counter sometimes returns nothing for a sample.
- Looping animations (status rings, equaliser) run only while their view is on screen, at 20-30 fps, because the see-through window is repainted in software.

- Spring animation on expand, ease-in-out on collapse; collapse starts 250 ms after the mouse leaves (`CollapseDelay`) and is animated: the card content fades out, the pill shrinks over about half a second, the capsule content fades in as it finishes, and the bubbles shrink away.
- **Moving the island:** press and hold anywhere on the pill (not on a button or dot) and drag it where you like, for example below a browser's tab strip. The position is saved and restored at the next start (`IslandLeft` / `IslandTop` in the settings file, in DIPs). It snaps to the default top-centre spot when within about 14 DIPs, and is clamped so the window always stays on the visible desktop (also re-clamped when the resolution or monitors change). Reset with Settings > Position > Reset position or the tray menu. A press that moves less than 5 DIPs is a click, not a drag (code: `MainWindow.Position.cs`).
- Starts on the primary monitor, top centre. Pure black pill.
- The window is non-activating (never takes focus), hidden from the taskbar and Alt+Tab, and always on top.
- The window is 480 x 310 DIPs, sized for the biggest card plus the bubbles; the area around the island is fully transparent, so clicks pass through to apps underneath.

## Coding agent status (Claude Code)
The island shows your Claude Code sessions: what each is doing, how long the current turn has run, and when one needs you. Read-only: you still answer in the terminal.

**Idle sessions stay visible:** while a Claude Code session is open but not working, the capsule shows it with an empty ring, the project name and "idle" (media and Focus still take priority over an idle session). The idle island with PC stats returns when no session is open.

**Session name:** the project folder (`CLAUDE_PROJECT_DIR`), not the hook's `cwd`, because `cwd` changes with every `cd` the agent runs.

**Capsule and bubble:** see "Behaviour" above. With one active session the capsule shows its project, a timer, and "needs your permission" plus the tool name when it waits. With several, it shows the avatars, "N sessions" and who needs you. The headline session is the one that needs you, otherwise the one that has worked longest. The Claude symbol is the official one (`ClaudeSymbol` geometry in `App.xaml`, from Wikimedia Commons "Claude AI symbol.svg"); it is Anthropic's trademark, so keep it out of public marketing material.

**Card (340 wide):** every session in one list (up to 5; waiting first, then working, then idle): ring avatar with the project's first letter, project, current action, duration, and a count chip. Ring: spinning arc = working, breathing full ring = needs you, empty = idle.

**Pop-ups (standard 270 x 60, two lines, 3 s):** amber "<project> needs you / Permission: Bash"; green "Done · 02:14 / <project>" when a turn that lasted 5 s or more finishes.

### How it connects
```
Claude Code hook event --> island-hook.exe --> named pipe "DynamicIsland.Agent" --> AgentService --> UI
```
- `DynamicIsland.Hook` (`island-hook.exe`, in the `hook` folder next to the island) reads the event JSON from stdin and forwards a small summary: event, session id, project folder, tool name, a short detail (file name, first 60 chars of a command, search pattern), notification text. **Prompts and file contents are never sent.** It never writes output and always exits 0, so it cannot change how Claude Code behaves; if the island is not running it gives up after 150 ms.
- The pipe is `PipeOptions.CurrentUserOnly`: no network, no port, only your Windows user.
- Hook events used: `SessionStart`, `SessionEnd`, `UserPromptSubmit`, `PreToolUse` (what it is doing), `PostToolUse` (detects that a permission prompt was answered), `Notification` (permission prompts only; the "waiting for input" prompt after a finished turn is ignored), `Stop` (turn finished).
- **Connect:** Settings > Integrations > Claude Code hooks > **Connect Claude Code**. This edits `~/.claude/settings.json`: it adds the seven entries (identified by `island-hook` in the command), keeps everything else, and saves a one-time backup as `settings.json.island-backup`. **Disconnect** removes only those entries. Invalid JSON is never touched. Comments in the file are not preserved when it is rewritten. It applies to **new** Claude Code sessions.
- Toggle: Settings > Integrations > Claude Code status (on by default).
- Running `island-hook.exe` by hand (double-click or an empty terminal) does nothing and exits at once, because it only acts on an event piped in by Claude Code.
- **Diagnosing "nothing shows":** Settings > Claude Code hooks shows "Last event received N s ago". "No events received" means no Claude Code session has reached the island: hooks are read when a session starts, so start a new one after clicking Connect. The island keeps sessions in memory, so after restarting the island a session reappears on its next event.

### Limits
- An interrupted turn (Esc) sends no `Stop` event, so a session silent for 10 minutes while "working" is treated as idle; idle sessions are forgotten after 1 hour.
- Each tool call runs the hook twice (Pre and Post), about 50-80 ms of .NET startup each. It needs the .NET 8 runtime (installed on this PC).
- Troubleshooting the hook: set `ISLAND_HOOK_DEBUG=1`; errors go to `%TEMP%\island-hook.log`. `ISLAND_CLAUDE_SETTINGS` points the installer at another settings file (used by tests). `ISLAND_PIPE_NAME` makes both the island and the hook use another pipe name, so a second island (for example for screenshots) can be fed simulated sessions without the real Claude sessions appearing in it; `SKYLAND_HIDE_MEDIA=1` makes the island ignore the media player (no song or artwork on screen). The hook ignores a UTF-8 byte-order mark in its input, which some shells add when piping.
- Tested: the pipe, state machine and pop-ups through the real hook exe with simulated events, and the Connect/Disconnect logic on a throwaway settings file (19 checks). Not yet tested live with real Claude Code sessions.

## Microsoft Clock sync (Focus sessions and timers)
The island mirrors the Windows Clock app, read-only: you start, pause and stop sessions in Clock, the island shows them.

- **Capsule:** progress ring with the minutes left, "Focus" (or "Break", the timer name) with "session 1 of 3" under it, and the countdown on the right. Orange = focus, green = break, blue = timer.
- **Bubble:** the ring with the minutes left, when Claude is the main capsule.
- **Card:** a large glowing progress ring holding the countdown, a colored phase chip (FOCUS / BREAK / TIMER), the title ("Session 1 of 3", or the timer name), a subtitle ("Up next: 5 min break", "Ends at 10:44 AM", or "Paused in Clock"), and wide session bars (solid = done, dimmed = current). Amber subtitle text means the values are frozen (Clock minimized or on another page).
- Focus ranks below Claude and above music (see Behaviour).
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
When the charger is connected the island flashes a standard 270 x 60 pill for 3 s: green bolt, green "Charging", and the battery percentage. Unplugging shows a neutral "Unplugged" with a battery outline and the percentage. Afterwards the island returns to whatever it was showing (stats or media).

- Driven by `SystemEvents.PowerModeChanged` (`StatusChange`), so there is no polling. The handler compares against the last known AC state, so battery-level changes do not trigger it.
- Takes priority over every other state, including expanded.
- Not shown on PCs without a battery, and not at app start.
- Fixed 3 s duration and always on: there is no Settings option yet.
- State machine: `IslandState.Notice`; `ShowPowerNotice()` in `MainWindow.xaml.cs`. Some laptops report the change a second or two after plugging in.

## PC stats
Shown in three places: the **idle island** (a row of icon + percentage for CPU, memory, GPU and battery), the **bottom row of the main card**, and the **System card** (ring gauges, opens when you hover the idle island). The optional performance bubble and the alert strip use the same readings. Icons are 12x12 vector `Path`s in `MainWindow.xaml` (no icon font); hover an icon for its name.

| Item | Source | Notes |
|------|--------|-------|
| CPU | `GetSystemTimes` delta | total load across all cores |
| RAM | `GlobalMemoryStatusEx` | physical memory in use |
| GPU | "GPU Engine" performance counters | busiest engine (like Task Manager); hidden if Windows exposes no counters. Works for NVIDIA, AMD and Intel, no vendor tools |
| Battery | `GetSystemPowerStatus` | hidden on PCs without a battery; a bolt appears on the icon when plugged in |

- A value turns red at 85% or more (battery below 20% while unplugged).
- Sampling runs all the time so the alerts can work: every 2 s while the numbers are on screen (idle island, a card, the performance bubble) and every 5 s otherwise. The first reading appears about 0.6 s after start, because CPU and GPU are deltas.
- Temperature is not included: CPU temperature needs LibreHardwareMonitor and usually administrator rights. Deferred on purpose.
- Requires the `System.Diagnostics.PerformanceCounter` NuGet package (for the GPU counters only).

## Settings window
Open it from the tray (double-click, or right-click > Settings...). Changes apply immediately; there is no Save button.

| Section | Setting | Default | Effect |
|---------|---------|---------|--------|
| General | Start with Windows | off | Adds/removes the Run registry value |
| General | Position: Reset position | top centre | Forgets the dragged position (also in the tray menu) |
| Island | Expand when you: Hover / Click | Hover | Hover expands on mouse enter; Click expands on click of the compact pill. Both collapse 250 ms after the mouse leaves |
| Island | Pop up on track change | off | Expands for 3 s when the track changes (not on app start, pause or resume) |
| Island | Performance ring: Off / Rings / CPU | Off | Ring-gauge bubble on the left of the island |
| Island | Heavy load and low battery alerts | on | Alert strip under the capsule (5 s, then silent for 2 minutes) |
| Integrations | Sync Microsoft Clock | on | Mirrors Clock's Focus sessions and timers (see above) |
| Integrations | Claude Code status | on | Shows Claude Code sessions (needs the hooks) |
| Integrations | Claude Code hooks: Connect / Disconnect | not connected | Adds or removes the island's hooks in `~/.claude/settings.json` |
| Updates | Check for updates / Install x.y.z | n/a | Looks for a newer release now; installs it when one is found |
| Updates | Check for updates automatically | on | On start and about daily; you are asked before installing |

**Adding a setting later:** add a property to `AppSettings`, add a row to a section in `SettingsWindow.xaml` (use the `Card`, `RowTitle`, `RowHint`, `ToggleSwitch` styles), save in its `Click` handler, and read `SettingsService.Current` where the feature runs. For a new group, add a `SectionLabel` plus a `Card`.

## Code map
| File | Role |
|------|------|
| `App.xaml(.cs)` | Startup, single-instance mutex, tray lifetime, shared `IconButton` style |
| `Services/TrayService.cs` | Tray icon (drawn in code, WinForms `NotifyIcon`): double-click or "Settings..." opens Settings, "Exit" quits |
| `Services/StartupService.cs` | Autostart via `HKCU\Software\Microsoft\Windows\CurrentVersion\Run` (no admin needed). The registry is the source of truth, not the settings file |
| `MainWindow.Agent.cs` | Coding agent UI: Claude capsule, bubble, session list card, status rings and avatars, pop-ups |
| `MainWindow.Stats.cs` | PC stats sampling, System card gauges, performance bubble, heavy-load and battery alerts |
| `Services/AgentService.cs` | Named-pipe server and Claude Code session state machine |
| `Services/ClaudeHookInstaller.cs` | Connect / Disconnect: merges the hooks into `~/.claude/settings.json` |
| `../DynamicIsland.Hook/` | `island-hook.exe`, the tiny program Claude Code runs on each hook event |
| `Services/UpdateService.cs` | GitHub release check, SHA-256 verified download, runs the MSI |
| `MainWindow.Intro.cs` | The start-up logo animation |
| `MainWindow.Position.cs` | Drag to move, snap to the default spot, clamp to the screen, restore on start |
| `MainWindow.Update.cs` | The update prompt and progress in the island, and the check schedule |
| `../Installer/Package.wxs`, `build-installer.ps1` | WiX package definition and the script that builds the MSI and its checksum |
| `Services/ClockSyncService.cs` | Reads Microsoft Clock's Focus session and timers via UI Automation (read-only) |
| `Services/SystemStatsService.cs` | CPU / RAM / GPU / battery sampling for the idle pill |
| `Services/SettingsService.cs` | `AppSettings` stored as JSON in `%AppData%\DynamicIsland\settings.json`; `SettingsService.Current` is read live by the island |
| `SettingsWindow.xaml(.cs)` | Dark single-page settings window with sections. Toggle and segmented-button styles live in `App.xaml` |
| `MainWindow.xaml(.cs)` | Island UI, state machine (Idle / Capsule / Card / Notice / Update / Intro), priority of activities, bubbles, animations, hover and button handling |
| `Services/MediaService.cs` | Wraps SMTC: snapshot (title, artist, artwork, play state), timeline, play/pause/next/previous |
| `Native/NativeMethods.cs` | `WS_EX_TOOLWINDOW` and `WS_EX_NOACTIVATE` window styles |
| `app.manifest` | Per-monitor DPI awareness |

## Design decisions
- `MediaService` is the only code that touches WinRT, so future features (volume HUD, notifications) can be added as separate services without changing the UI plumbing.
- Overlapping media events are handled with a version counter, so the latest refresh wins.
- Artwork is only reloaded when title, artist or album changes.
- Progress position is extrapolated from SMTC `LastUpdatedTime`, because players update it only occasionally.

## Installer (MSI) and updates

### What the installer does
`Installer\build-installer.ps1` builds `dist\SkyLand-<version>.msi` (about 69 MB, self-contained, no .NET needed) with WiX Toolset 5 (`dotnet tool install --global wix --version 5.0.2`, plus the Util extension). WiX 5 is used on purpose: WiX 6/7 ask the user to accept the Open Source Maintenance Fee terms.
- **Per-user** install to `%LocalAppData%\Programs\SkyLand`: no admin prompt, which is what lets SkyLand update itself silently.
- Start Menu and Desktop shortcuts; the SkyLand icon in Apps & features.
- Starts SkyLand when it finishes, with the `--installed` switch: the app then turns **Start with Windows on, the first time only** (`AutostartDefaultApplied` in settings). An update never re-enables it after you turned it off.
- Same `UpgradeCode` for every version: a newer MSI replaces the installed one (major upgrade); installing an older one is refused.
- Closes a running SkyLand (and a running `island-hook.exe`) before replacing files.
- **Uninstall** removes the files, both shortcuts and the Start with Windows entry (it runs `SkyLand.exe --uninstall` first). Your settings (`%AppData%\DynamicIsland`) and your Claude Code hook entries are left alone; Disconnect in Settings removes the hooks.
- If you move from the portable copy in `D:\Apps` to the installed one, the Start with Windows entry and the Claude hook paths are repointed automatically on first start.

### Release process
1. Bump `<Version>` in `DynamicIsland\DynamicIsland.csproj` (the only place the version lives).
2. `cd Installer; .\build-installer.ps1` creates `dist\SkyLand-x.y.z.msi` and `dist\SkyLand-x.y.z.msi.sha256`.
3. After `gh auth login`: `gh release create vX.Y.Z dist\SkyLand-X.Y.Z.msi dist\SkyLand-X.Y.Z.msi.sha256 --repo cbsajan/skyland --title "SkyLand X.Y.Z" --notes "..."`.
   The tag must be `v` plus the version, and **both files must be attached**: the app only offers a release that has the `.msi` and a matching `.msi.sha256`.
4. Signing is optional and off for now: `.\build-installer.ps1 -CertPath C:\keys\skyzack.pfx` signs `SkyLand.exe` and the MSI with `signtool` (Windows SDK). The password is asked at the prompt or read from `SKYLAND_CERT_PASSWORD`; nothing secret is stored in the repo.

### How the in-app updater works (`Services/UpdateService.cs`, `MainWindow.Update.cs`)
- Checks the latest release of `cbsajan/skyland` through the GitHub API: 20 s after the app starts, then every 6 h but at most about once a day. Turn off in Settings > Updates > Check for updates automatically; the **Check for updates** button always works.
- A newer version shows a prompt in the island: **"Update 1.1.0 ready" [Install] [Later]**. Later hides it for 24 h. Install shows download progress in the island, then runs the MSI (`msiexec /i ... /passive`, no admin prompt), which closes the app, replaces the files and starts the new version.
- **Verification:** the installer is downloaded over HTTPS from GitHub hosts only, and run only if its SHA-256 equals the expected hash; a mismatch deletes the download and installs nothing. While releases are **unsigned** this protects against corrupted or swapped downloads but not against someone who controls the GitHub repository; a code-signing certificate would close that gap (the next step would be to make the updater check the signature too).
- **Where the expected hash comes from:** the `digest` field GitHub adds to each release asset in the API answer (computed by GitHub on upload), so the check needs no second request. Only if that field is missing does it download the release's `.msi.sha256` file. The check has its own 20 s limit (the HTTP client's 10 minutes are for the download). A 403 or 429 answer (GitHub allows only 60 anonymous requests per hour per IP address, which a shared network can use up) shows "GitHub is limiting update checks from this network. Try again in a few minutes."; any other failure shows "Could not reach the update server." Reason: fetching the `.sha256` file redirects to `release-assets.githubusercontent.com`, and on this PC .NET needed about 20 s for that first connection (curl needed 1 s), most likely a certificate revocation lookup; the check felt stuck. The installer download itself still goes to that host, so its first bytes can take a while to arrive.
- Test seam: environment variable `SKYLAND_UPDATE_URL` replaces the GitHub URL (and lifts the host allow-list) so the flow can be tested against a local server. Never set it in normal use.
- Tested end to end on this PC: install 1.0.0 silently, detect and install 1.0.1 through the app's own Install button (about 18 s, new version running, autostart/hooks/shortcuts intact), a download with a wrong checksum refused, then uninstall (everything removed).
- Real GitHub path: the check was measured against the published 1.1.0 release (0.4-0.7 s with the API digest). The in-app Install of a real release has not been tested yet.

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
dotnet publish -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true -p:DebugType=none -o D:\Apps\SkyLand
```
Run `D:\Apps\SkyLand\SkyLand.exe`, then turn on **Start with Windows** in Settings from that copy, so the registry entry points at the stable path. Quit the running island (tray, Exit) before republishing, or the exe is locked. Only one instance can run, so a `dotnet run` copy and the published copy cannot both be open.
Quit from the tray icon (right-click, Exit). Launching a second copy exits immediately.

## Known gaps / next ideas
- "Start with Windows" registers the exe path it is currently running from. If that is `bin\Debug`, rebuilding or moving the project breaks autostart. Publish to a stable folder (`dotnet publish -c Release`) and enable it from there.
- Primary monitor only; the position is computed once at startup and does not follow resolution changes.
- Ideas for later: volume/brightness/charging HUD, auto-pop on track change, notification mirroring, settings.
