<p align="center">
  <a href="README.md">简体中文</a> ·
  <b>English</b> ·
  <a href="README.ja.md">日本語</a>
</p>

# Wuwatappo Native

Wuwatappo Native is the Windows-native implementation of WuwaTappo 1.0.2, written in C# / .NET 8 with WinForms and WebView2, connecting to Kuro Games' official desktop pet service; this project is not an official Kuro Games project.

> The in-app English/Japanese UI is currently a preview; some interface text may still appear in Chinese. The Windows installer UI is Chinese-only.

## Feature Overview at a Glance

**Implemented features:**

- ✅ Supports all online content and multiplayer from the original version
- ✅ Supports switching between CN and Global servers, plus official login inheritance
- ✅ Supports Global server browser verification login (email / X / Google verified; other methods untested and require self-testing)
- ✅ Supports Simplified Chinese / English / Japanese UI (English and Japanese are previews; some text remains in Chinese)
- ✅ Supports CN server login with phone number + SMS verification code (Kuro Games Pass)
- ✅ Supports customizing all skins (in other players' rooms only the skins you actually own are shown, but any skin can be set locally)
- ✅ Supports sending any emote (regardless of ownership status or character restrictions, visible to others)
- ✅ Supports headwear position, free scaling, and rotation fine-tuning, saved independently per headwear item
- ✅ Supports custom companions: local decorative pets, random batch creation, multi-select deletion, independent positions, and temporary hiding
- ✅ Supports character and accessory appearance rotation at a configurable interval
- ✅ Supports chest notification filters by rarity and category
- ✅ Supports quickly hiding other players' pets from the tray menu
- ✅ Supports automatically generating a full color scheme from a theme color, a smooth color picker, and automatic color extraction from images
- ✅ Supports control panel background image upload, opacity/blur adjustment, and theme preset saving with automatic rotation
- ✅ Supports independent bottom bar color schemes for the main desktop pet and remote characters; keyboard, rhythm game, Wuthering Waves, and gamepad views all follow consistently
- ✅ Supports hiding the bottom bars of the main desktop pet and remote characters separately, toggled with single-key or two-key shortcuts
- ✅ Supports panel maximization and eight-direction edge resizing, and remembers the panel size and maximized state
- ✅ Supports F8 multi-screen switching: three strategies — default-position members, main character only, and all members — with proportional mapping across resolutions
- ✅ Supports Ctrl multi-select of multiplayer pets and custom companions, Ctrl+A select all, group dragging with edge snapping, and clicking empty space to exit the selection
- ✅ Supports viewing all announcements currently issued by the server on the settings page (including read status)
- ✅ Supports refreshing the Key count (+10000 with one click), and automatically topping up when it is below 1000
- ✅ Supports automatic rank-up synthesis and one-click bulk rank-up
- ✅ Supports stable / beta update channels
- ✅ Supports automatic treasure chest claiming
- ✅ Supports auto emote, auto click, and game mode rules
- ✅ Supports hotkeys and settings search
- ✅ Supports a controller protocol extension for PS / Switch and other DirectInput controllers when no XInput controller is detected, including wired DualSense sticks and analog triggers (single controller)
- ✅ Supports displaying the current keyboard/gamepad key bindings, following the theme colors
- ✅ Rhythm game mode supports 4K / 5K / 6K, with key bindings saved separately for each mode; Space and punctuation keys such as ; ' , . / can be configured
- ✅ Supports one-click inheritance of the official login state
- ✅ Supports streamer mode OBS capture window (custom background color / transparent background)
- ✅ Supports automatic avoidance and virtual-grid snapping (main character excluded; left/right anchor, right by default) for multiplayer characters and custom companions, plus horizontal/vertical arrangement and order reset
- ✅ Supports showing/hiding multiplayer members individually, with settings saved per member
- More optimized features waiting for you to discover......

**Detail optimizations:**

- Memory optimization: in the same scenario (12 people on screen), the official version uses 650 MB of RAM, while this version uses 80 MB
- Multiplayer saves each person's position: if the other party disconnects and rejoins, or you restart the software, everyone stays in their original position
- Live stream capture optimization: uses a separate capturable window with customizable window resolution to avoid low image quality

Demo video: <https://www.bilibili.com/video/BV1nRxk6dERS/>

If this project is helpful to you, a Star would be appreciated, and you are also welcome to like, coin, and favorite it on Bilibili. QQ group: 1130030892.

## Star History ⭐⭐⭐

<a href="https://www.star-history.com/?repos=danieltoyama%2Fwuwatapponative&type=date&releases=&legend=bottom-right">
 <picture>
   <source media="(prefers-color-scheme: dark)" srcset="https://api.star-history.com/chart?repos=danieltoyama/wuwatapponative&type=date&theme=dark&legend=bottom-right" />
   <source media="(prefers-color-scheme: light)" srcset="https://api.star-history.com/chart?repos=danieltoyama/wuwatapponative&type=date&legend=bottom-right" />
   <img alt="Star History Chart" src="https://api.star-history.com/chart?repos=danieltoyama/wuwatapponative&type=date&legend=bottom-right" />
 </picture>
</a>

## Installation

### Using the Release Package

Download from [GitHub Releases](https://github.com/DanielToyama/WuwaTappoNative/releases):

> The installer UI is currently Chinese-only; the option names quoted below are shown exactly as they appear in the installer.

- `WuwatappoNative-<version>-Setup.exe`: installs to `%LOCALAPPDATA%\Programs\Wuwatappo Native`; it includes an administrator manifest by default, shows the install mode on startup and defaults to "为所有用户安装（推荐）", requesting UAC after selection so that the desktop pet running in administrator mode can be closed; you can also choose a standard-permission install for the current user only.
- `WuwatappoNative-<version>-portable.zip`: extract and run `WuwatappoNative.exe` directly.

### Beta Builds

Every push to `main` that changes source code, assets, or build scripts included in the beta package triggers the GitHub Actions `Beta build`, which automatically generates a beta version with a beta version number and publishes it as a Pre-release on GitHub Releases. Changes only to docs, workflows, test code, etc. will not regenerate the beta package.

- Beta version number format: `<next patch version>-beta.<run_number>`, for example `0.3.3-beta.123`.
- The beta Release title and description include the time this build was pushed (UTC+8), making it easy to distinguish multiple updates within the same rolling Release.
- Download option 1: open [GitHub Releases](https://github.com/DanielToyama/WuwaTappoNative/releases), find the `v<next patch version>-beta` marked as **Pre-release** (for example `v0.3.3-beta`), and download the `WuwatappoNative-<beta version>-Setup.exe` or `WuwatappoNative-<beta version>-portable.zip` for the corresponding beta version number.
- Download option 2: open the latest run of [Beta build Actions](https://github.com/DanielToyama/WuwaTappoNative/actions/workflows/beta.yml) and download `WuwatappoNative-beta-<run_number>` from **Artifacts** at the bottom of the page; the archive contains both the Setup and the portable version.
- Beta builds provide both an installer and a portable version; like the stable version, the installer supports choosing "为所有用户安装" or "仅当前用户安装", and the portable version runs `WuwatappoNative.exe` after extraction. Beta builds do not provide automatic updates and do not guarantee stability or compatibility.
- Commits for stable releases no longer trigger new beta package builds, preventing beta versions from being mixed into a stable release.
- Both beta and stable builds can choose stable or beta in `Settings → Updates → Update channel`: the stable channel only receives stable versions, while the beta channel receives both stable versions and subsequent beta builds. After an update is found, it still must be downloaded and installed manually; there is no silent upgrade.
- Beta and stable builds share the `%LOCALAPPDATA%\WuwatappoNative` configuration directory; before testing, it is recommended to exit any running old instance first, and if you are concerned about configuration changes, back up that directory first.
- When providing feedback, include the beta version number, reproduction steps, and relevant logs, and submit them to [GitHub Issues](https://github.com/DanielToyama/WuwaTappoNative/issues). **Do not upload tokens, account credentials, or screenshots containing credentials.**
- For daily use, download the stable version (non-Pre-release) from Releases; beta builds do not affect update checks for the stable version.

System requirements:

- Windows 10/11 x64.
- WebView2 Runtime. It is usually preinstalled on Windows 10/11; if missing, the panel cannot be displayed, but the desktop pet itself can still run.
- Using a release package does not require installing the .NET Runtime.

Before installing or launching a new version, exit any running old instance from the tray first.

## Quick Start

1. Launch the program. On first launch, the main panel opens and displays a disclaimer; after you confirm it, the program remembers that state.
2. Log in from the panel:
   - **UID + verification code**: enter the 9–10 digit game UID, then obtain and enter the 6-digit verification code.
   - **Phone number + SMS verification code**: on the CN server, you can switch to **Phone number** on the login page and log in with your Kuro Games Pass phone number.
   - **Global server browser verification login**: after selecting the Global server, follow the login page and complete email / X / Google verification in the browser; other login methods are untested and require self-testing.
   - **Inherit the official program login state**: open `Settings → Account → Inherit official client login`; the program reads the login data of the official WuwaTappo on this machine and closes the running official client.
   - **Import token**: you can use the standalone tool in `token-extractor/` to read `kuro_infoToken`, then paste it into settings to import.
3. In `Settings → Account → Server region`, choose CN or Global; switching regions logs out the current account and requires logging in again.
4. Open `Multiplayer` and create a room or enter a room number to join. After joining successfully, the room members are displayed in the member list on the right. When a member rejoins the room, the saved position is restored first; if it overlaps an existing member by more than 30% of the visible area, it is rearranged according to the current horizontal/vertical sorting. Members without a saved position are also assigned a position according to the sorting scheme.
- The host's room is automatically identified from the `rm` snapshot and persisted; automatic room rejoin is based solely on the server's `user/me.room_no`, and local records do not override the server state. When automatically rejoining the host's room, the program connects directly to the room socket and does not call `rooms/join`; a second confirmation prompt appears before manually leaving or switching the host's room.
5. The first run creates the following files:

```text
%LOCALAPPDATA%\WuwatappoNative\config.json
%LOCALAPPDATA%\WuwatappoNative\state.json
%LOCALAPPDATA%\WuwatappoNative\click_ledger.json
%LOCALAPPDATA%\WuwatappoNative\logs\app-YYYY-MM-DD.log
%LOCALAPPDATA%\WuwatappoNative\logs\synth-YYYY-MM-DD.jsonl
%LOCALAPPDATA%\WuwatappoNative\logs\diag-YYYY-MM-DD.log
%LOCALAPPDATA%\WuwatappoNative\cache\
```

`config.json` stores login state, appearance, and settings; `state.json` stores window position, room state, member positions, and member visibility; `click_ledger.json` stores cumulative clicks and clicks pending reporting per account/server; `cache\` stores appearance and image caches.

Log directory details:

- `logs\app-YYYY-MM-DD.log`: normal runtime log; records the version/OS/screen DPI/key settings at startup; with `--verbose`, additionally outputs `DBG` debug lines. When a single file exceeds 16 MB, an `app-YYYY-MM-DD.1.log` volume is created.
- `logs\synth-YYYY-MM-DD.jsonl`: synthesis snapshots, one JSON operation per line. Each time an automatic/manual synthesis is actually initiated, records the operation id, settings, initial backpack materials, per-round consumption and responses, result, and elapsed time; automatic empty checks (where no synthesis is initiated) are not written. A single file is split into volumes when it exceeds 32 MB.
- `logs\diag-YYYY-MM-DD.log`: diagnostic log, including z-order/Issue #13 on-site snapshots (top-level window order, desktop pet topmost/cloaked state, `WindowFromPoint` hits, external windows above), WebView2 failures, etc.; a single file is split into volumes when it exceeds 16 MB.
- All three log sets keep only the last 7 calendar days (including today) and are cleaned up automatically based on the last write time; a legacy root-level `log.txt` is migrated to the app log for the corresponding date at startup, or deleted directly if it is more than 7 days old.
- None of the logs record credentials such as tokens, cookies, or Authorization headers.

## Usage and Configuration

### Panel and Desktop Pet

The panel contains six pages: `Bag`, `Gallery`, `Synthesis`, `Presets`, `Multiplayer`, and `Settings`.

Desktop pet operations:

- Drag the desktop pet to move it; dragging within about 28 pixels of the screen or taskbar edge automatically snaps it to the edge.
- Right-click the desktop pet to open a menu containing Open panel, Emote wheel, Always on top, Translucent on hover, Click-through, Pet size, Pet opacity, Reset position, Restart pet, and Exit.
- Right-click a multiplayer character to separately adjust **Remote pet size** and **Remote pet opacity**; these two settings apply uniformly to all multiplayer characters and do not affect the main character.
- `Settings → Pet & window` lets you adjust pet size 25%–200%, pet opacity, remote pet size and opacity (both follow the main pet by default and can also be customized separately), remote player name size (50%–200%), animation frame rate (30 / 60 / 120, default 60), mouse pass-through, translucent on hover, hover fade amount (10%–100%, default 40%), and OBS capture window size, background color/transparent background, compatibility mode, and "Stop rendering the desktop pet".
- The multiplayer character style and chest-opening button on the Multiplayer page have been moved into Settings.
- When mouse pass-through is enabled, the desktop pet does not receive mouse clicks; you can restore it from the right-click menu or with `F7`.
- The OBS capture window's title is `Wuwatappo Capture`, and it can be captured in OBS using Window Capture; the background can be set to a custom color code or made transparent (transparent mode requires checking "Allow Transparency" in OBS Window Capture (shown as "允许透明" in the Chinese OBS UI)). On first enable, it prompts you to change the capture method to Windows 10 (1903 or later); if OBS still shows a black shadow, you can enable compatibility mode to fall back to normal window rendering.
- "Restart pet" exits the current process and restarts it, with the single-instance lock handed over automatically.

### Default Hotkeys

| Function | Default key |
|---|---|
| Game mode (mouse pass-through) toggle | `F7` |
| Switch screen | `F8` |
| Show/hide desktop pet | `F9` |
| Refresh UI | `F5` |
| Send emote slots 1–6 | `Ctrl+Alt+1` to `Ctrl+Alt+6` |

Features such as `OBS capture window`, `Open main panel`, `Toggle rhythm mode`, `Toggle auto emote`, and `Toggle auto click` do not occupy keys by default and can be bound as needed on the settings page. `Open main panel` returns to the `Bag` home page and does not keep the previously viewed page.

Hotkeys can be recorded, cleared, or reset to defaults in `Settings → Desktop keyboard → Hotkeys`; changes take effect immediately after saving. Hotkeys are passively monitored via Raw Input and do not register system-level hotkeys, so they will not conflict with other software (if they do conflict, both will trigger).

### Settings

| Settings location | Contents |
|---|---|
| `Settings → Account` | Server region, login, inherit official login, import token, log out, refresh account info, cumulative click top-up |
| `Settings → Pet & window` | Pet size, remote pet size/follow main pet, pet opacity, remote pet opacity/follow main pet, remote player name size, animation frame rate, always on top, mouse pass-through, hover hide, grid alignment (left/right), overlap handling, OBS capture window size/background color/transparent background/compatibility mode/stop rendering the desktop pet |
| `Settings → Desktop keyboard` | 26-key, Wuthering Waves, Rhythm mode, Controller, controller protocol extension, key mapping, and hotkeys |
| `Settings → Multiplayer & chests` | Remote pet layout, hide other members, automatic rank-up synthesis, manually open treasure chests, treasure chest display, automatic treasure chest claiming |
| `Multiplayer` page | Create/join/switch rooms, room history, member display, quick emotes |
| `Settings → Automation` | Auto emote, auto click, auto click rate, and virtual tap animation |
| `Settings → Notifications` | Chest claim notification, hotkey action notification |
| `Settings → Other` | Launch at login, run as administrator, automatic game mode on fullscreen, game mode rules, update channel, update download source, HTTP proxy, check for updates |

The emote source can be Quick bar random, Specific emote, or Whole emote library random, with a send rate cap of 1–20 times per second. The auto click rate is 1–10 clicks per second, and Show virtual tap animation can be set separately.

`Settings → Other → Game mode rules` accepts process names, for example `WutheringWaves.exe`. When a rule matches, the program automatically enables mouse pass-through and restores it when the process exits; name matching is case-insensitive, and `.exe` may be included or omitted.

### Startup and Administrator Privileges

- `Settings → Other → Launch at login` controls startup for the current user.
- Standard privileges use `HKCU\Software\Microsoft\Windows\CurrentVersion\Run`.
- Administrator privileges use a highest-privilege scheduled task triggered by the current user's logon, so no UAC prompt appears at logon.
- `Settings → Other → Run as administrator` is used to receive Raw Input from foreground programs running with administrator privileges; starting it manually requests UAC.

### Updates

"Check for updates" at the bottom of the settings page reads GitHub Releases. When the current version is several versions behind, the update notes list the changes for those versions from newest to oldest and scroll within the update banner on the settings page. Update notes support Chinese / English / Japanese, and only the section for the current interface language is shown. The program does not install updates automatically; you need to download the new Setup or portable version from the Release page.

Version compatibility status page: <https://code.danieluu.top/WuwaTappoNative/>. Both the page and the client read the same `docs/compatibility.json`, which is used to show whether the current version is still within the safe compatibility range.

## Performance

Under a stress test with 44 members and 10 key presses per second for all members, it uses about 6.7% of a single core with no action backlog; when dragging characters, the keyboard only moves without repainting. Stress test command:

```powershell
WuwatappoNative.exe --stress 44 --stress-hz 10
```

Bubble animations use a UI animation clock that is enabled only while active, coalescing key presses, counts, and animation repaints, and caching thumbnails of the current character, bottom bar, and bubbles; it does not modify the system's global timer resolution. The animation frame rate can be set to 30 / 60 / 120 in settings (actually about 30 / 61 / 120 times per second), taking effect immediately after switching; the higher the frame rate, the more frequently the desktop pet window repaints and presents. You can run the offline animation regression checks and rendering benchmark:

```powershell
dotnet run --project tests/AnimationChecks/AnimationChecks.csproj -c Release
```

The checks do not start the main application, display the desktop pet, or read account configuration. Clock frequency and off-screen rendering time are not equal to the actual displayed FPS, nor do they represent compatibility conclusions for game topmost, ray tracing, or frame generation.

## Limitations

- Phone number login is only supported on the CN server; for the Global server, use browser verification login (email / X / Google verified; other methods require self-testing), inheritance of the official client login, or import a token.
- A Global server Pass account can exchange for an account-level token login even without a bound game character; some features that depend on character/player information may be unavailable.
- When the official client uses App-Bound Encryption (v20), its cookies cannot be decrypted, so use UID verification code, phone number login, or Global server browser verification instead.
- A process running with standard privileges cannot receive Raw Input from an elevated foreground window; if you need this scenario, enable launching with administrator privileges.
- Multiplayer, opening treasure chests, and account information depend on Kuro Games servers; the related features are unavailable when the network is unreachable or the servers are unavailable.
- Only Windows 10/11 x64 is supported.
- This project is not an official Kuro Games project; copyright in the game assets belongs to Kuro Games. See [NOTICE.md](NOTICE.md) and [assets/README.md](assets/README.md) for details.


### Building from Source

Requirements:

- Windows 10/11 x64
- Git
- .NET 8 SDK

```powershell
git clone https://github.com/DanielToyama/WuwaTappoNative.git
cd WuwaTappoNative
dotnet build -c Release
.\bin\Release\net8.0-windows\WuwatappoNative.exe
```

The repository already includes the default assets and offline appearance directories needed for building and running; no additional asset packages need to be downloaded.

The main program and the standalone login extraction tool both pin `SQLitePCLRaw.bundle_e_sqlite3` 2.1.14 and actually load native SQLite 3.54.0, which has been patched for [CVE-2025-6965](https://github.com/advisories/GHSA-2m69-gcr7-jv3q). You can re-check the dependency graph and read compatibility as needed:

```powershell
dotnet list WuwatappoNative.csproj package --vulnerable --include-transitive
dotnet list token-extractor/WuwatappoTokenExtractor.csproj package --vulnerable --include-transitive
dotnet run --project tests/SqliteChecks/SqliteChecks.csproj -c Release
```

The check scripts use only an in-memory database and do not read any real cookies or account data from the local machine.

### Building the Installer

Requires PowerShell 7 and Inno Setup 6.7 or later.

```powershell
pwsh -File .\installer\build.ps1 -Version 0.3.7
```

Output:

```text
dist/WuwatappoNative-0.3.7-Setup.exe
dist/WuwatappoNative-0.3.7-portable.zip
dist/WuwatappoTokenExtractor-1.1.2-x64.zip
```

## License

- The source code is copyright the project author and is provided for learning and research use only, without any warranty of any kind; see [LICENSE](LICENSE) and [NOTICE.md](NOTICE.md) for details.
- "Wuthering Waves", WuwaTappo, and all game assets are copyright Kuro Games; this project has no affiliation with Kuro Games. Assets in the repository are only used to build and run this program and should not be extracted or redistributed separately.
- See [THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md) for third-party components and licenses.
