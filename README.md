![Emutastic](Emutastic/Assets/banners%20and%20icons/emutastic-banner-scaled.png)

# Emutastic

[![License: GPL v3](https://img.shields.io/badge/License-GPLv3-blue.svg)](https://www.gnu.org/licenses/gpl-3.0)

A full-featured multi-system emulator frontend for Windows — built with WPF and .NET 8 — that turns your game collection into a clean, console-organized library spanning **37 systems** from the 8-bit era through **PlayStation 3**. Emulation is handled by [libretro](https://www.libretro.com/) cores loaded at runtime; no cores or BIOS files are bundled.

**Two ways to play:** a full-power desktop library at your monitor, or **EmuTV** — a controller-only, big-screen mode for the couch.

Also available for [Linux](https://github.com/codingncaffeine/Emutastic-For-Linux) and [Apple Silicon Mac](https://github.com/codingncaffeine/Emutastic-for-Mac).

**[Visit emutastic.com →](https://www.emutastic.com/emutasticapp.html)** for a visual tour of the app, or grab the [latest release](https://github.com/codingncaffeine/Emutastic/releases) directly.

## Highlights

- 🎮 **37 systems** — 8-bit classics through **PlayStation 3** (PS2 via LRPS2, PS3 via RPCS3)
- 🗂️ **Clean, console-organized library** with box art and rich metadata (OpenVGDB + ScreenScraper)
- 🏆 **RetroAchievements** — full hardcore-mode compliance, in-game unlock toasts, and per-game stats
- 📺 **EmuTV** — a controller-only, couch-friendly console mode for the living room (renders ES-DE themes)
- 🎨 **Deep theming** — a visual editor with live preview and 44 color tokens
- ☁️ **GitHub cloud sync** — battery saves and your library follow you across PCs, with optional encryption
- 🔧 **Built-in ROM patching** — apply IPS / BPS / UPS hacks and translations at launch, original ROM left untouched
- 📖 **In-app game manuals** · 🎥 **gameplay recording** · 📝 **per-game notes**
- 🎛️ **Full controller support** — analog-as-D-pad, gamepad save states, disk swapping, and per-system cheats

## 📺 EmuTV — the living-room mode

![EmuTV](Emutastic/Assets/banners%20and%20icons/emutv_banner.png)

EmuTV turns Emutastic into a **couch-first console interface** — browse your entire library, launch games, and load save states with **only a controller**, no keyboard or mouse. Think Steam Big Picture or LaunchBox Big Box, built for your TV and living-room setup.

It renders **[ES-DE](https://es-de.org/) themes** out of the box — carousels, wheels, grids, box art, and metadata — so your shelf looks exactly how you want it. Same Emutastic underneath; a completely different experience on the couch.

> **Legal notice:** This project is a frontend only. It does not include, distribute, or facilitate the acquisition of any copyrighted software, ROM images, BIOS files, or other proprietary system files. You are solely responsible for ensuring you have the legal right to use any software you load into this application.

---

## Requirements

- Windows 10/11 x64
- [.NET 8 Desktop Runtime](https://dotnet.microsoft.com/en-us/download/dotnet/8.0)
- [Visual C++ Redistributable 2015–2022 (x64)](https://aka.ms/vs/17/release/vc_redist.x64.exe) — required by most libretro cores
- libretro core `.dll` files (downloadable in-app — Preferences → Cores)
- `SDL3.dll` (x64) for controller name detection (downloadable in-app — Preferences → Extras)
- Optional: `ffmpeg.exe` for video recording, DAT files for ROM identification (also in Preferences → Extras)

> **Windows SmartScreen:** Emutastic is not code-signed. Click **"More info"** then **"Run anyway"** on first launch.

---

## Supported Systems

<details>
<summary><strong>37 systems across 11 manufacturers</strong> (click to expand)</summary>

| System | Tag | Core (priority order) | BIOS |
|---|---|---|---|
| NES | NES | nestopia → quicknes → fceumm | No |
| Famicom Disk System | FDS | nestopia | `disksys.rom` |
| SNES | SNES | snes9x → bsnes | No |
| Nintendo 64 | N64 | parallel_n64 → mupen64plus_next | No |
| GameCube | GameCube | dolphin | No |
| Game Boy | GB | mgba → gambatte → sameboy | No |
| Game Boy Color | GBC | mgba → gambatte → sameboy | No |
| Game Boy Advance | GBA | mgba | Optional |
| Nintendo 3DS | 3DS | azahar | No |
| Nintendo DS | NDS | desmume → melonds | No |
| Virtual Boy | VirtualBoy | mednafen_vb | No |
| Genesis / Mega Drive | Genesis | genesis_plus_gx → picodrive | No |
| Sega CD / Mega CD | SegaCD | genesis_plus_gx | `bios_CD_U.bin` (USA), `bios_CD_E.bin` (EU), `bios_CD_J.bin` (JP) |
| Sega 32X | Sega32X | picodrive | No |
| Sega Saturn | Saturn | mednafen_saturn → kronos → yabause | Kronos: `kronos/saturn_bios.bin`. Beetle: `sega_101.bin` / `mpr-17933.bin` (JP), `mpr-17941.bin` (USA/EU) |
| Master System | SMS | genesis_plus_gx → picodrive | No |
| Game Gear | GameGear | genesis_plus_gx | No |
| SG-1000 | SG1000 | genesis_plus_gx | No |
| Dreamcast | Dreamcast | flycast | No |
| PlayStation | PS1 | mednafen_psx_hw → mednafen_psx | USA: `scph5501.bin` / `scph1001.bin` / `scph7001.bin`, EU: `scph5502.bin`, JP: `scph5500.bin` |
| PlayStation 2 | PS2 | pcsx2 (LRPS2) | Region dump, any filename (e.g. `SCPH-39001.bin`) |
| PlayStation 3 | PS3 | RPCS3 (installed via Cores / Extras) | System firmware (`PS3UPDAT.PUP`), user-provided |
| PSP | PSP | ppsspp | No |
| TurboGrafx-16 | TG16 | mednafen_pce → mednafen_pce_fast | No |
| TurboGrafx-CD | TGCD | mednafen_pce → mednafen_pce_fast | Any of `syscard3.pce` / `syscard2.pce` / `syscard1.pce` |
| Neo Geo Pocket | NGP | mednafen_ngp | No |
| Neo Geo Pocket Color | NGPC | mednafen_ngp | No |
| Neo Geo | NeoGeo | geolith | `neogeo.zip` + `aes.zip` |
| Neo Geo CD | NeoCD | geolith | `neogeo.zip` + `aes.zip` + `neocdz.zip` |
| Arcade | Arcade | fbneo + mame2003-plus | No |
| Atari 2600 | Atari2600 | stella | No |
| Atari 7800 | Atari7800 | prosystem | No |
| Atari Jaguar | Jaguar | virtualjaguar | No |
| ColecoVision | ColecoVision | gearcoleco → bluemsx | No |
| Vectrex | Vectrex | vecx | No |
| 3DO | 3DO | opera | Any of `panafz10.bin` (Panasonic) / `panafz1j.bin` (JP) / `goldstar.bin` (GoldStar) |
| Philips CD-i | CDi | same_cdi | No |

</details>

For per-system BIOS filenames and placement, known-broken arcade hardware, the Saturn BIOS naming nuance, and Neo Geo / Neo Geo CD specifics, see the **[Wiki](https://github.com/codingncaffeine/Emutastic/wiki)**.

---

## ROM Import

Drag and drop ROMs onto the library or use **Import ROMs**. The app detects the console from file extension, cleans the title, and hashes the ROM. For ambiguous formats (`.chd`, `.iso`, `.cue`, `.bin`), a SHA1 lookup against DAT files is attempted first — if no match, a console picker is shown.

**Multi-disc games** (Final Fantasy VII, Metal Gear Solid, etc.) are auto-bundled into a single library entry — drop a folder containing the disc files (`.cue`/`.bin` or `.chd`) and Emutastic writes an `.m3u` playlist alongside them so the game shows up once, not three times. Hand-authored `.m3u` files in the folder are honored as-is.

**Important:** Download DAT files in **Preferences → Cores / Extras** before importing. Without them, disc images and some cartridge ROMs may be assigned to the wrong system during import.

---

## Features

<details>
<summary><strong>Click to expand the full feature list</strong></summary>

<br>

<details>
<summary><strong>EmuTV — Big-Screen Couch Mode</strong></summary>

A controller-only, 10-foot front end for the TV. Open it from the library with the **L3 + R3 + L2 + R2** chord (the same chord quits a running game back to EmuTV). Browse your consoles and games, launch with a button, and pull up save states with **Start** — no keyboard or mouse needed.

EmuTV renders themes built for **[EmulationStation Desktop Edition (ES-DE)](https://es-de.org/)**: drop an ES-DE theme into your EmuTV themes folder, or download one from the built-in theme browser (press **Y**), which pulls from the official ES-DE themes list. Carousels (including wheel layouts), grids, text lists, game metadata, rating stars, favorite/completed badges, and WebP artwork all render per the ES-DE spec.

> ES-DE is a large, evolving spec — not every theme is fully supported yet, so some may render with missing or imperfect elements. The bundled **EmuTV Default** theme always works as a known-good fallback. Rebind EmuTV's controls and review every controller combo in **Preferences → EmuTV**.

</details>

<details>
<summary><strong>Themes</strong></summary>

Four built-in themes: **Dark** (default), **Light**, **OLED Black**, **Midnight Blue**. Full visual editor with 44 color tokens and live preview. Set custom background images with zoom, pan, and tile controls. Export/import themes as `.emutheme` files.

</details>

<details>
<summary><strong>Artwork & Metadata</strong></summary>

Box art, titles, developers, genres, and descriptions are filled in automatically — **no account required**. By default Emutastic matches your games against **OpenVGDB**, a built-in local database, and pulls box art from the **libretro thumbnail server**. (Only the OpenVGDB match is offline; the artwork itself still downloads over the internet.)

Sign in to **ScreenScraper** in **Preferences → Snaps** to promote it to the primary source — community-edited, region-aware metadata with fuller coverage, plus **3D box art** and downloadable **game manuals**. OpenVGDB stays on as the backup that fills anything ScreenScraper misses.

</details>

<details>
<summary><strong>Controllers</strong></summary>

XInput button polling during gameplay with SDL3 device name detection. Xbox, DualSense/DualShock, and hundreds of other controllers are identified by product name. Button mappings configurable per-controller in **Preferences → Input**. Falls back to generic names if `SDL3.dll` is absent.

**Left analog stick works as movement input** on every old console with a digital joystick or D-pad — push the stick on the NES, SNES, Genesis, Game Boy line, Saturn, Neo Geo, Atari, ColecoVision, TurboGrafx, arcade games, and more, and your character moves. Diagonals are honored (pushing NE registers as up + right simultaneously). The D-pad still works exactly as before — use whichever you prefer.

**Save and load states from the gamepad** — hold **L3** then press **R2** to save, or **L2** to load your latest state, in any game (no overlay needed). A quick on-screen toast confirms each action, and the buttons are configurable per console in **Preferences → Controls**.

</details>

<details>
<summary><strong>RetroAchievements</strong></summary>

Earn achievements while playing via [RetroAchievements](https://retroachievements.org/). Enable in **Preferences → Achievements** with your RA username and password; paste your Web API Key (from retroachievements.org → Settings) in the same place to unlock per-game stats on the detail card.

The detail card for any game you've launched with achievements enabled shows:

- An **achievement progress bar** with `X / Y unlocked · Z pts`, gold-tinted when you've mastered the set
- **Coming up** — three suggested achievement badges. If you've made in-game progress in your last session, these are the ones you're closest to ("73% · 3 of 5"); otherwise they're picked from the community's fastest-typical unlocks for the game
- **Typical run** caption: `beat ~Xh · master ~Yh` based on community medians
- Hardcore mode aware — all numbers and "Coming up" picks reflect hardcore unlocks when Hardcore is on

In-game, achievements appear as toast notifications when you unlock them.

**Hardcore mode** — Emutastic enforces every RetroAchievements hardcore-mode rule (save-state loading blocked, cheats blocked, no rewind/slow-motion/frame-advance features, unique User-Agent, persistent on-screen indicator). Server-side hardcore credit requires RA's formal approval, which can be applied for once a frontend has been publicly available for six months — Emutastic's window opens October 14, 2026. See the [Hardcore Compliance](https://github.com/codingncaffeine/Emutastic/wiki/Hardcore-Compliance) wiki page for a line-by-line audit of every requirement on RA's checklist, with code cross-references.

</details>

<details>
<summary><strong>About & Updates</strong></summary>

**Preferences → About** shows the current version, build date, and credits. On open, it checks GitHub for the latest release — if a newer version is available, you can download and install it in-app. The update is staged in a temp folder and applied by a small companion updater (`Emutastic.Updater.exe`) that replaces the running binary while the app restarts. No telemetry.

</details>

- **Core Options** — Per-core settings (internal resolution, graphics plugins, etc.) in **Preferences → Core Options**
- **Play Time Tracking** — The game detail card shows your total accumulated play time per game, recorded each session

<details>
<summary><strong>Cloud Sync</strong></summary>

Sync battery saves and your library database across PCs using your GitHub account. The library database carries your game metadata, ratings, favorites, play time, and **per-game notes**, so all of that follows you to a second PC. Sign in with one click in **Preferences → Backups** — a private `emutastic-saves` repo is created automatically under your account. Battery saves upload on game close and download on game launch; only newer files transfer. Full bidirectional sync available via **Sync Now**.

Optional **AES-256-GCM encryption** with a user-chosen passphrase — saves are encrypted before they leave your machine. Your saves repo is a normal private GitHub repo you can browse anytime. See the [Cloud Sync](https://github.com/codingncaffeine/Emutastic/wiki/Cloud-Sync) wiki page for details on encryption, storage limits, sharing saves, and troubleshooting.

</details>

<details>
<summary><strong>Disk Swapping (FDS, PS1, Saturn, Sega CD)</strong></summary>

Press **L3 + Start** in-game to flip between discs/sides on systems that need it. Rebindable to any two-button chord (controller or keyboard) in **Preferences → Controls → Disk Swap**. The status bar shows the new disc number on each swap.

Multi-disc games are auto-bundled at import time — see the [ROM Import](#rom-import) section. See the [wiki page](https://github.com/codingncaffeine/Emutastic/wiki/Disk-Swapping) for per-console specifics and troubleshooting.

</details>

<details>
<summary><strong>Game Notes</strong></summary>

Keep free-form notes on any game — passwords, where you left off, strategies — in a floating editor with line numbers, find, and word-wrap/monospace toggles. Open notes from the library right-click menu, a game's detail card, or the in-game overlay. Notes autosave as you type and ride your Cloud Sync backup across PCs. The window can be pinned on top and rolled up to its title bar — handy beside a running game on a single monitor.

</details>

<details>
<summary><strong>Game Manuals</strong></summary>

Download a game's original PDF manual and read it in a built-in viewer — zoom, search, page thumbnails — that reopens on your last-read page. Pull it up in-game from the overlay without closing your game. Manuals are sourced from ScreenScraper (requires a ScreenScraper login); coverage is best for popular console titles.

</details>

<details>
<summary><strong>Cheats</strong></summary>

Per-game cheats from the in-game cog menu or the library detail card's `⋯` menu. Game Genie / GameShark / raw codes depending on system. See **[Cheats](https://github.com/codingncaffeine/Emutastic/wiki/Cheats)** in the wiki for code formats per system, storage paths, and the list of cores where cheats aren't supported.

</details>

<details>
<summary><strong>ROM Hacks</strong></summary>

Apply an IPS, BPS, or UPS patch to a base game right from the library (right-click → **Apply ROM Hack**). The patched game becomes its own library entry — with its own saves — while your original ROM is left untouched, so there's no second copy on disk. The patch is applied in memory at launch, and BPS/UPS patches are checksum-verified against your ROM, so a mismatched or wrong-region copy is caught before it loads. Available on cartridge systems (SNES, GBA, Game Boy / Game Boy Color, NES, Genesis, Nintendo 64, and more).

See **[ROM Hacks](https://github.com/codingncaffeine/Emutastic/wiki/ROM-Hacks)** in the wiki for the full system list, supported patch formats, how patched ROMs are scraped, and tips on matching a patch to the right base ROM.

</details>

</details>

---

## Wiki

Per-system configuration, known issues, teardown fixes, and technical details are documented in the **[Wiki](https://github.com/codingncaffeine/Emutastic/wiki)**.

---

## Building

Requires Visual Studio 2022+ with **.NET desktop development** workload.

```
git clone <repo>
cd Emutastic
dotnet build .\Emutastic.sln -c Release
```

---

<details>
<summary><strong>Credits</strong></summary>

### Libretro Cores

Emulation is handled by libretro cores maintained by their upstream authors. Emutastic bundles none of them — the in-app core manager downloads from the libretro build servers on demand. Please support these projects directly.

| Core | Upstream author(s) |
|---|---|
| Azahar | Azahar team (successor to Citra / Lime3DS) |
| Beetle PSX / Saturn / PCE / VB / NGP | Mednafen team (Ryphecha) |
| blueMSX | blueMSX team (Daniel Vik and contributors) |
| bsnes | byuu / near and contributors |
| DeSmuME | DeSmuME team |
| Dolphin | Dolphin team |
| FBNeo (FinalBurn Neo) | FBNeo team |
| MAME 2003-Plus | MAME team / libretro contributors |
| FCEUmm | FCEUmm team |
| Flycast | flyinghead and contributors |
| Gambatte | Sindre Aamås (sinamas) |
| Gearcoleco | Ignacio Sánchez (drhelius) |
| Genesis Plus GX | Eke-Eke |
| Geolith | R. Danbrook (rdanbrook) |
| Kronos | Kronos team |
| LRPS2 | PCSX2 team / libretro maintenance |
| melonDS | Arisotura |
| mGBA | Vicki Pfau (endrift) |
| Mupen64Plus-Next | libretro team |
| Nestopia UE | Nestopia UE team |
| Opera | libretro team (3DO) |
| ParaLLEl-N64 | libretro team (Themaister and contributors) |
| Picodrive | notaz |
| PPSSPP | Henrik Rydgård and contributors |
| ProSystem | Greg Stanton (upstream) / libretro maintenance |
| QuickNES | Shay Green (blargg) |
| RPCS3 (PlayStation 3, experimental — standalone emulator, fetched on demand from its own builds and run as a separate process) | RPCS3 team |
| SAME CDi | CDi community (MAME derivative) |
| Snes9x | Snes9x team |
| Stella | Stella team |
| VecX | Valavan Manohararajah (upstream) / libretro maintenance |
| Virtual Jaguar | Virtual Jaguar team |
| Yabause | Yabause team |

### Libraries

| Library | Purpose | License |
|---|---|---|
| [rcheevos](https://github.com/RetroAchievements/rcheevos) | RetroAchievements client | MIT |
| [libchdr](https://github.com/rtissera/libchdr) | CHD format reader (CHD-based achievement hashing) | BSD 3-Clause |
| [libwebp (dwebp)](https://github.com/webmproject/libwebp) | Google's WebP decoder — renders WebP art in EmuTV themes | BSD 3-Clause |

Full license texts in `NOTICES.txt`.

### Shaders & Bezels

Optional downloadable extras (Preferences → Cores/Extras). Emutastic bundles none of these — the in-app downloader fetches them from the sources below on demand.

| Project | Purpose | License |
|---|---|---|
| [librashader](https://github.com/SnowflakePowered/librashader) | Runtime that renders slang shader presets | MPL-2.0 (runtime) / MIT (headers) |
| [libretro slang-shaders](https://github.com/libretro/slang-shaders) | Community multi-pass shader preset collection | Various (per shader) |
| [The Bezel Project](https://github.com/thebezelproject) | Arcade & Neo Geo bezel artwork | Community artwork |

### EmuTV Themes

EmuTV renders themes built for [EmulationStation Desktop Edition (ES-DE)](https://es-de.org/), and the in-app theme browser downloads from the official [ES-DE themes list](https://gitlab.com/es-de/themes/themes-list). Themes are created by their respective authors under their own licenses — please support them on their project pages. Emutastic is not affiliated with or endorsed by ES-DE.

### Controller Illustrations
Artwork from [OpenEmuControllerArt](https://github.com/kodi-game/OpenEmuControllerArt) (BSD 3-Clause). Not affiliated with or endorsed by OpenEmu.

| Artist | Controllers |
|---|---|
| **David McLeod** ([@Mucx](https://twitter.com/Mucx/)) | 32X, FDS, GB, GBA, Game Gear, SMS, NES, Sega CD, Genesis, SNES |
| **Ricky Romero** ([@RickyRomero](https://twitter.com/RickyRomero/)) | Atari 2600/5200, N64, NDS, Odyssey², PS1, PSP, Saturn, SG-1000, Vectrex, Virtual Boy |
| **Craig Erskine** ([@qrayg](https://twitter.com/qrayg/)) | GameCube, Neo Geo Pocket, PC Engine / TG16 |
| **Salvo Zummo** / **David Everly** / **Kate Schroeder** | Atari 7800, 3DO, ColecoVision |

Emutastic's clean, library-first design was inspired by [OpenEmu](https://openemu.org/), the superb multi-system emulator for macOS — the project that started it all. Not affiliated with or endorsed by OpenEmu.

</details>

---

## License

[GNU General Public License v3.0](LICENSE)
