![Emutastic](Emutastic/Assets/banners%20and%20icons/emutastic-banner-scaled.png)

# Emutastic

[![License: GPL v3](https://img.shields.io/badge/License-GPLv3-blue.svg)](https://www.gnu.org/licenses/gpl-3.0)

A multi-system emulator frontend for Windows built with WPF and .NET 8, inspired by [OpenEmu](https://openemu.org/) on macOS. Games are organized by console in a clean library interface. Emulation is handled by [libretro](https://www.libretro.com/) cores loaded at runtime — no cores are bundled.

**[Visit emutastic.com →](https://www.emutastic.com/emutasticapp.html)** for a visual tour of the app, or grab the [latest release](https://github.com/codingncaffeine/Emutastic/releases) directly.

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
<summary><strong>36 systems across 11 manufacturers</strong> (click to expand)</summary>

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
| Sega CD / Mega CD | SegaCD | genesis_plus_gx | Region BIOS |
| Sega 32X | Sega32X | picodrive | No |
| Sega Saturn | Saturn | mednafen_saturn → kronos → yabause | Region BIOS |
| Master System | SMS | genesis_plus_gx → picodrive | No |
| Game Gear | GameGear | genesis_plus_gx | No |
| SG-1000 | SG1000 | genesis_plus_gx | No |
| Dreamcast | Dreamcast | flycast | No |
| PlayStation | PS1 | mednafen_psx_hw → mednafen_psx | Region BIOS |
| PlayStation 2 | PS2 | pcsx2 (LRPS2) | Region BIOS |
| PSP | PSP | ppsspp | No |
| TurboGrafx-16 | TG16 | mednafen_pce → mednafen_pce_fast | No |
| TurboGrafx-CD | TGCD | mednafen_pce → mednafen_pce_fast | `syscard3.pce` |
| Neo Geo Pocket | NGP | mednafen_ngp | No |
| Neo Geo Pocket Color | NGPC | mednafen_ngp | No |
| Neo Geo | NeoGeo | geolith | `neogeo.zip` + `aes.zip` |
| Neo Geo CD ² | NeoCD | geolith | `neogeo.zip` + `aes.zip` + `neocdz.zip` |
| Arcade | Arcade | fbneo + mame2003-plus ¹ | No |
| Atari 2600 | Atari2600 | stella | No |
| Atari 7800 | Atari7800 | prosystem | No |
| Atari Jaguar | Jaguar | virtualjaguar | No |
| ColecoVision | ColecoVision | gearcoleco → bluemsx | No |
| Vectrex | Vectrex | vecx | No |
| 3DO | 3DO | opera | `panafz10.bin` |
| Philips CD-i | CDi | same_cdi | No |

</details>

¹ Arcade routes per-game between FBNeo (primary; better controls + save states) and MAME 2003-Plus (fills FBNeo gaps — Atari vector, Sega G-80, Cinematronics, Williams pre-MK, Killer Instinct, War Gods, etc.). MAME 2003-Plus's romset expectations have drifted over time and a few drivers (Sega ST-V, Midway Vegas) aren't fully backported — your mileage will vary on those. See the [Arcade wiki page](https://github.com/codingncaffeine/Emutastic/wiki/Arcade) for known-broken hardware.

² Neo Geo CD plays via Geolith with full library support (import, BIOS validation, controls, artwork from ScreenScraper/libretro thumbnails). RetroAchievements identification works; **achievement triggers won't fire during play yet** — that depends on a CD-mode byteswap shadow buffer landing in Geolith upstream. The cart side has it (shipped earlier today); the CD-side patch is pending. Cart achievements (Metal Slug, KOF, etc.) work today. See the [Neo Geo wiki page](https://github.com/codingncaffeine/Emutastic/wiki/Neo-Geo) for the current status.

---

## BIOS Files

Place BIOS files in `%AppData%\Emutastic\System\` (or `PortableData\System\` next to the .exe in portable mode). The app also checks each system's ROM folder.

<details>
<summary><strong>BIOS file details by system</strong></summary>

**Sega CD** — `bios_CD_U.bin` (USA), `bios_CD_E.bin` (Europe), `bios_CD_J.bin` (Japan)

**Sega Saturn** — Kronos: `system\kronos\saturn_bios.bin`. Beetle Saturn: `sega_101.bin` (JP v1.00), `mpr-17933.bin` (JP v1.01), `mpr-17941.bin` (USA/EU v1.01). Note: `mpr-17933.bin` is a Japan BIOS despite being commonly mislabeled as USA/EU.

**PlayStation** — USA: `scph5501.bin`, `scph1001.bin`, `scph7001.bin`. Europe: `scph5502.bin`. Japan: `scph5500.bin`

**TurboGrafx-CD** — Any of: `syscard3.pce`, `syscard2.pce`, `syscard1.pce`

**3DO** — Any of: `panafz10.bin` (Panasonic), `panafz1j.bin` (Japan), `goldstar.bin` (GoldStar)

**Famicom Disk System** — `disksys.rom`

</details>

---

## ROM Import

Drag and drop ROMs onto the library or use **Import ROMs**. The app detects the console from file extension, cleans the title, and hashes the ROM. For ambiguous formats (`.chd`, `.iso`, `.cue`, `.bin`), a SHA1 lookup against DAT files is attempted first — if no match, a console picker is shown.

**Multi-disc games** (Final Fantasy VII, Metal Gear Solid, etc.) are auto-bundled into a single library entry — drop a folder containing the disc files (`.cue`/`.bin` or `.chd`) and Emutastic writes an `.m3u` playlist alongside them so the game shows up once, not three times. Hand-authored `.m3u` files in the folder are honored as-is.

**Important:** Download DAT files in **Preferences → Cores / Extras** before importing. Without them, disc images and some cartridge ROMs may be assigned to the wrong system during import.

---

## Features

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

---

## Folder Layout

```
Emutastic.exe / rcheevos.dll / .NET runtime DLLs
```

```
%AppData%\Emutastic\          (or your custom data folder)
    library.db
    Native\                   (SDL3.dll, ffmpeg.exe — downloadable in-app)
    DATs\                     (No-Intro / Redump DATs — downloadable in-app)
    Cores\                    (libretro core DLLs — downloadable in-app)
    System\                   (BIOS files)
    Save States\ / BatterySaves\ / Screenshots\ / Recordings\ / Artwork\ / ...
```

### Portable mode

Drop an empty `portable.txt` next to `Emutastic.exe` **or** launch with the `--portable` command-line flag, and **everything** lives in `PortableData\` beside the .exe — config, library database, save states, battery saves, screenshots, recordings, artwork, BIOS files, libretro cores, and any ROMs you import. Move the install folder to a USB stick and run it on any Windows PC; library paths are stored relative to `PortableData\` so drive-letter changes (E:→F:) don't break anything. ROM imports are auto-copied into `PortableData\Roms\<Console>\` so they travel with the USB without setting up a separate library folder. See **[Portable Mode](https://github.com/codingncaffeine/Emutastic/wiki/Portable-Mode)** in the wiki for the full on-disk layout, caveats, and how to revert.

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

Full license texts in `NOTICES.txt`.

### Shaders & Bezels

Optional downloadable extras (Preferences → Cores/Extras). Emutastic bundles none of these — the in-app downloader fetches them from the sources below on demand.

| Project | Purpose | License |
|---|---|---|
| [librashader](https://github.com/SnowflakePowered/librashader) | Runtime that renders slang shader presets | MPL-2.0 (runtime) / MIT (headers) |
| [libretro slang-shaders](https://github.com/libretro/slang-shaders) | Community multi-pass shader preset collection | Various (per shader) |
| [The Bezel Project](https://github.com/thebezelproject) | Arcade & Neo Geo bezel artwork | Community artwork |

### Controller Illustrations
Artwork from [OpenEmuControllerArt](https://github.com/kodi-game/OpenEmuControllerArt) (BSD 3-Clause). Not affiliated with or endorsed by OpenEmu.

| Artist | Controllers |
|---|---|
| **David McLeod** ([@Mucx](https://twitter.com/Mucx/)) | 32X, FDS, GB, GBA, Game Gear, SMS, NES, Sega CD, Genesis, SNES |
| **Ricky Romero** ([@RickyRomero](https://twitter.com/RickyRomero/)) | Atari 2600/5200, N64, NDS, Odyssey², PS1, PSP, Saturn, SG-1000, Vectrex, Virtual Boy |
| **Craig Erskine** ([@qrayg](https://twitter.com/qrayg/)) | GameCube, Neo Geo Pocket, PC Engine / TG16 |
| **Salvo Zummo** / **David Everly** / **Kate Schroeder** | Atari 7800, 3DO, ColecoVision |

Inspired by [OpenEmu](https://openemu.org/) for macOS.

</details>

---

## License

[GNU General Public License v3.0](LICENSE)
