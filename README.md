![Emutastic](Emutastic/Assets/banners%20and%20icons/emutastic-banner-scaled.png)

# Emutastic

[![License: GPL v3](https://img.shields.io/badge/License-GPLv3-blue.svg)](https://www.gnu.org/licenses/gpl-3.0)

A multi-system emulator frontend for Windows built with WPF and .NET 8, inspired by [OpenEmu](https://openemu.org/) on macOS. Games are organized by console in a clean library interface. Emulation is handled by [libretro](https://www.libretro.com/) cores loaded at runtime — no cores are bundled.

> **Legal notice:** This project is a frontend only. It does not include, distribute, or facilitate the acquisition of any copyrighted software, ROM images, BIOS files, or other proprietary system files. You are solely responsible for ensuring you have the legal right to use any software you load into this application.

---

## Requirements

- Windows 10/11 x64
- [.NET 8 Desktop Runtime](https://dotnet.microsoft.com/en-us/download/dotnet/8.0)
- [Visual C++ Redistributable 2015–2022 (x64)](https://aka.ms/vs/17/release/vc_redist.x64.exe) — required by most libretro cores
- libretro core `.dll` files in the `Cores\` folder (downloadable in-app)
- `SDL3.dll` (x64) next to the executable — for controller name detection ([download](https://github.com/libsdl-org/SDL/releases))

> **Windows SmartScreen:** Emutastic is not code-signed. Click **"More info"** then **"Run anyway"** on first launch.

---

## Supported Systems

<details>
<summary><strong>33 systems across 10 manufacturers</strong></summary>

| System | Tag | Core (priority order) | BIOS |
|---|---|---|---|
| NES | NES | nestopia → quicknes → fceumm | No |
| Famicom Disk System | FDS | nestopia | `disksys.rom` |
| SNES | SNES | snes9x → bsnes | No |
| Nintendo 64 | N64 | parallel_n64 → mupen64plus_next | No |
| GameCube | GameCube | dolphin | No |
| Game Boy | GB | mgba → gambatte | No |
| Game Boy Color | GBC | mgba → gambatte | No |
| Game Boy Advance | GBA | mgba | Optional |
| Nintendo 3DS | 3DS | azahar | No |
| Nintendo DS | NDS | desmume → melonds | No |
| Virtual Boy | VirtualBoy | mednafen_vb | No |
| Genesis / Mega Drive | Genesis | genesis_plus_gx → picodrive | No |
| Sega CD / Mega CD | SegaCD | genesis_plus_gx | Region BIOS |
| Sega 32X | Sega32X | picodrive | No |
| Sega Saturn | Saturn | kronos → mednafen_saturn → yabause | Region BIOS |
| Master System | SMS | genesis_plus_gx → picodrive | No |
| Game Gear | GameGear | genesis_plus_gx | No |
| SG-1000 | SG1000 | genesis_plus_gx | No |
| Dreamcast | Dreamcast | flycast | No |
| PlayStation | PS1 | mednafen_psx → pcsx_rearmed | Region BIOS |
| PSP | PSP | ppsspp | No |
| TurboGrafx-16 | TG16 | mednafen_pce → mednafen_pce_fast | No |
| TurboGrafx-CD | TGCD | mednafen_pce → mednafen_pce_fast | `syscard3.pce` |
| Neo Geo Pocket / Color | NGP | mednafen_ngp | No |
| Neo Geo | NeoGeo | geolith | `neogeo.zip` + `aes.zip` |
| Arcade | Arcade | fbneo → mame variants | No |
| Atari 2600 | Atari2600 | stella | No |
| Atari 7800 | Atari7800 | prosystem | No |
| Atari Jaguar | Jaguar | virtualjaguar | No |
| ColecoVision | ColecoVision | gearcoleco → bluemsx | No |
| Vectrex | Vectrex | vecx | No |
| 3DO | 3DO | opera | `panafz10.bin` |
| Philips CD-i | CDi | same_cdi | No |

</details>

---

## BIOS Files

Place BIOS files in `%AppData%\Roaming\Emutastic\system\`. The app also checks each system's ROM folder.

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

**Important:** Download DAT files in **Preferences → Cores / Extras** before importing. Without them, disc images and some cartridge ROMs may be assigned to the wrong system during import.

---

## Features

<details>
<summary><strong>Themes</strong></summary>

Four built-in themes: **Dark** (default), **Light**, **OLED Black**, **Midnight Blue**. Full visual editor with 44 color tokens and live preview. Set custom background images with zoom, pan, and tile controls. Export/import themes as `.emutheme` files.

</details>

<details>
<summary><strong>Controllers</strong></summary>

XInput button polling during gameplay with SDL3 device name detection. Xbox, DualSense/DualShock, and hundreds of other controllers are identified by product name. Button mappings configurable per-controller in **Preferences → Input**. Falls back to generic names if `SDL3.dll` is absent.

</details>

<details>
<summary><strong>RetroAchievements</strong></summary>

Earn achievements while playing via [RetroAchievements](https://retroachievements.org/). Enable in **Preferences → Achievements** with your RA username and password. Achievements appear as toast notifications during gameplay.

</details>

- **Core Options** — Per-core settings (internal resolution, graphics plugins, etc.) in **Preferences → Core Options**

---

## Folder Layout

```
Emutastic.exe / SDL3.dll / rcheevos.dll
Cores\          (core DLLs — downloadable in-app)
DATs\           (No-Intro / Redump DATs — downloadable in-app)
```

```
%AppData%\Roaming\Emutastic\
    library.db / system\ / saves\ / screenshots\
```

---

## Wiki

Per-system configuration, known issues, teardown fixes, and technical details are documented in the **[Wiki](https://github.com/codingncaffeine/Emutastic/wiki)**.

---

## Building

Requires Visual Studio 2022+ with **.NET desktop development** workload.

```
git clone <repo>
cd Emutastic
dotnet build
```

---

<details>
<summary><strong>Credits</strong></summary>

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
