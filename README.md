![Emutastic](Emutastic/Assets/banners%20and%20icons/emutastic-banner-scaled.png)

# Emutastic

[![License: GPL v3](https://img.shields.io/badge/License-GPLv3-blue.svg)](https://www.gnu.org/licenses/gpl-3.0)

A multi-system emulator frontend for Windows built with WPF and .NET 8, inspired by [OpenEmu](https://openemu.org/) on macOS. Games are organized by console in a clean library interface. Emulation is handled by [libretro](https://www.libretro.com/) cores loaded at runtime — no cores are bundled.

> **Legal notice:** This project is a frontend application only. It does not include, distribute, or facilitate the acquisition of any copyrighted software, ROM images, BIOS files, or other proprietary system files. You are solely responsible for ensuring you have the legal right to use any software you load into this application. The authors of this project do not condone piracy.

---

## Windows SmartScreen

Emutastic is not code-signed, so Windows SmartScreen may block the app on first launch. Click **"More info"** then **"Run anyway"** to proceed. This is normal for unsigned open-source software.

---

## Requirements

- Windows 10/11 x64
- [.NET 8 Desktop Runtime](https://dotnet.microsoft.com/en-us/download/dotnet/8.0) *(not included with Windows — must be installed separately)*
- [Visual C++ Redistributable 2015–2022 (x64)](https://aka.ms/vs/17/release/vc_redist.x64.exe) — required by most libretro cores (Dolphin, PPSSPP, Kronos, etc.)
- libretro core `.dll` files placed in the `Cores\` folder next to the executable (core downloads are offered in-app)
- `SDL3.dll` (x64) placed next to the executable — used for controller name detection
  - Download from [libsdl.org](https://github.com/libsdl-org/SDL/releases) (Runtime Binaries → Windows x64)

---

## Supported Systems

| System | Console Tag | Core (priority order) | BIOS Required |
|---|---|---|---|
| Neo Geo | NeoGeo | geolith | `neogeo.zip` + `aes.zip` |
| Arcade | Arcade | fbneo *(recommended)* → fbalpha2012 → fbalpha2012_cps1/2/3/neogeo → mame2003_plus → mame2003 → mame2010 → mame2015 → mame2016 → mame → mame2000 | No |
| Nintendo Entertainment System | NES | nestopia → quicknes → fceumm | No |
| Famicom Disk System | FDS | nestopia | `disksys.rom` |
| Super Nintendo | SNES | snes9x → bsnes | No |
| Nintendo 64 | N64 | parallel_n64 → mupen64plus_next | No |
| GameCube | GameCube | dolphin | No |
| Game Boy | GB | mgba → gambatte | No |
| Game Boy Color | GBC | mgba → gambatte | No |
| Game Boy Advance | GBA | mgba | Optional (`gba_bios.bin`) |
| Nintendo DS | NDS | desmume → melonds | No |
| Virtual Boy | VirtualBoy | mednafen_vb | No |
| Sega Genesis / Mega Drive | Genesis | genesis_plus_gx → picodrive | No |
| Sega CD / Mega CD | SegaCD | genesis_plus_gx | Region BIOS (see below) |
| Sega 32X | Sega32X | picodrive | No |
| Sega Saturn | Saturn | kronos → mednafen_saturn → yabause | Region BIOS (see below) |
| Sega Master System | SMS | genesis_plus_gx → picodrive | No |
| Game Gear | GameGear | genesis_plus_gx | No |
| SG-1000 | SG1000 | genesis_plus_gx | No |
| PlayStation | PS1 | mednafen_psx → pcsx_rearmed | Region BIOS (see below) |
| PlayStation Portable | PSP | ppsspp | No |
| TurboGrafx-16 / PC Engine | TG16 | mednafen_pce → mednafen_pce_fast | No |
| TurboGrafx-CD / PC Engine CD | TGCD | mednafen_pce → mednafen_pce_fast | `syscard3.pce` |
| Neo Geo Pocket / Color | NGP | mednafen_ngp | No |
| Atari 2600 | Atari2600 | stella | No |
| Atari 7800 | Atari7800 | prosystem | No |
| Atari Jaguar | Jaguar | virtualjaguar | No |
| ColecoVision | ColecoVision | gearcoleco → bluemsx | No |
| Vectrex | Vectrex | vecx | No |
| 3DO | 3DO | opera | `panafz10.bin` / `panafz1j.bin` / `goldstar.bin` |
| Philips CD-i | CDi | same_cdi | No |

---

## BIOS Files

Place BIOS files in `%AppData%\Roaming\Emutastic\system\`. The app will also check the ROM folder for a given system, so you can co-locate BIOS files with your ROMs if preferred.

### Sega CD
Filenames must match exactly:
| Region | File |
|---|---|
| USA | `bios_CD_U.bin` |
| Europe | `bios_CD_E.bin` |
| Japan | `bios_CD_J.bin` |

### Sega Saturn
Kronos uses a single file placed in a subfolder:
```
system\kronos\saturn_bios.bin
```
Beetle Saturn / Mednafen accept region-specific files:
| Region | File |
|---|---|
| Japan v1.00 | `sega_101.bin` |
| Japan v1.01 | `mpr-17933.bin` |
| USA / Europe v1.01 | `mpr-17941.bin` |

> **Note:** `mpr-17933.bin` is a Japan BIOS. Several community guides incorrectly label it as USA/EU.

### PlayStation
| Region | Accepted files |
|---|---|
| USA | `scph5501.bin`, `scph1001.bin`, `scph7001.bin` |
| Europe | `scph5502.bin` |
| Japan | `scph5500.bin` |

### TurboGrafx-CD
Any one of: `syscard3.pce`, `syscard2.pce`, `syscard1.pce`

### 3DO
Any one of: `panafz10.bin` (Panasonic), `panafz1j.bin` (Japan), `goldstar.bin` (GoldStar)

### Famicom Disk System
`disksys.rom`

---

## ROM Import

Drag and drop ROM files onto the library window or use the **Import ROMs** button in the navigation bar. The app:

1. Detects the console from the file extension
2. For ambiguous formats (`.chd`, `.iso`, `.cue`), attempts a SHA1 lookup against [No-Intro / Redump DAT files](https://www.no-intro.org/) placed in the `DATs\` folder — if matched, the console is set automatically
3. If no DAT match is found, shows a console picker so you can assign it manually
4. Hashes the ROM with MD5, cleans the title (strips region/revision tags), and saves it to the library database

### Ambiguous Formats

| Extension | Candidate Systems |
|---|---|
| `.chd` | Sega CD, Saturn, PS1, TurboGrafx-CD, 3DO, Dreamcast, Philips CD-i |
| `.iso` | PSP, GameCube, 3DO |
| `.cue` | Sega CD, Saturn, PS1, TurboGrafx-CD, 3DO |

---

## Core Options

Per-core settings (internal resolution, graphics plugins, controller pak type, etc.) are accessible via **Preferences → Core Options tab**. Browse and edit options for any core without a game running.

> **Note:** Core options for a platform only appear after you have launched at least one game for that platform. On first launch, the core's available options are captured automatically — after that they persist and are editable any time.

Options are saved per-core. Some options (e.g. internal resolution on PPSSPP) require restarting the game to take effect.

---

## Controllers

Controller input uses XInput for button polling during gameplay and **SDL3** for device name detection. SDL3's built-in controller database correctly identifies Xbox (USB, Bluetooth, and Xbox Wireless Adapter), DualSense/DualShock, and hundreds of other controllers by their real product names.

If `SDL3.dll` is not present the app falls back to XInput slot enumeration with generic names ("Controller 1", etc.) — gameplay is unaffected.

Button mappings are configurable per-controller in **Preferences → Input**.

---

## Folder Layout

```
Emutastic.exe
SDL3.dll
rcheevos.dll
Cores\
    snes9x_libretro.dll
    dolphin_libretro.dll
    opera_libretro.dll
    ... (all other core DLLs)
DATs\              (created automatically — download DATs in-app via Preferences → Cores / Extras)
```

App data is stored at `%AppData%\Roaming\Emutastic\`:
```
%AppData%\Roaming\Emutastic\
    library.db         (SQLite game library)
    system\            (BIOS files go here)
    saves\             (save states and SRAM)
    screenshots\
```

---

## Core-Specific Notes

Detailed per-system notes — configuration, known issues, workarounds, and implementation details — are in the **[Wiki](https://github.com/codingncaffeine/Emutastic/wiki)**.

Highlights:
- **[[Nintendo 64|https://github.com/codingncaffeine/Emutastic/wiki/Nintendo-64]]** — ParaLLEl-RDP via Vulkan swapchain (4x/8x upscaling), teardown sequence, timing
- **[[GameCube|https://github.com/codingncaffeine/Emutastic/wiki/GameCube]]** — Dolphin libretro: OpenGL backend, CachedInterpreter, controller init order
- **[[Dreamcast|https://github.com/codingncaffeine/Emutastic/wiki/Dreamcast]]** — VMU saves (5 conditions), 30fps timing fix
- **[[RetroAchievements|https://github.com/codingncaffeine/Emutastic/wiki/RetroAchievements]]** — rcheevos integration pitfalls (CRT linking, auth, HTTP bridging)
- And more: Vectrex, Neo Geo, PSP, CD-i, disc-based systems, emulation timing

---

## RetroAchievements

Emutastic supports [RetroAchievements](https://retroachievements.org/) — earn achievements while playing retro games. Enable it in **Preferences → Achievements** with your RetroAchievements username and password. Achievements pop up as toast notifications during gameplay.

See the [RetroAchievements wiki page](https://github.com/codingncaffeine/Emutastic/wiki/RetroAchievements) for integration details.

---

## Themes

Emutastic includes a full theme system with a built-in visual editor — no external tools required.

### Built-in Themes
Four themes ship out of the box: **Dark** (default), **Light**, **OLED Black**, and **Midnight Blue**. Switch between them in **Preferences → Theme**.

### Theme Editor
Open the editor from **Preferences → Theme → Customize...** to tweak any of the 44 color tokens with a live preview panel that updates in real time. Adjust backgrounds, text, accents, scrollbars, buttons, and more — then apply or export your creation.

### Background Images
Set a custom background image behind your game library grid in **Preferences → Theme**. Control opacity and scaling to get the look you want. Background images are included when exporting themes.

### Sharing Themes
Export your theme as a `.emutheme` file (a ZIP containing colors, manifest, and optional assets) and share it with others. Import community themes by dragging a `.emutheme` file onto the app window or using the import button in Preferences.

### Console-Specific Colors
Enable **Console-specific colors** in Preferences to have accent and background colors automatically change as you browse different console libraries — green for Game Boy, blue for PlayStation, etc. Theme creators can define per-console overrides in their color sets.

---

## Known Limitations

### Philips CD-i — Analog Cursor Sensitivity
The original CD-i TV remote controller featured a thumbpad that provided proportional cursor movement — pushing gently gave fine control, pushing fully moved the cursor quickly across the screen. The SAME CDi libretro core internally maps the thumbpad to a digital 4-way joystick port in MAME, so analog stick input is currently thresholded to on/off directional presses rather than providing a true sensitivity curve.

This is a core-level limitation rather than a frontend issue. A proper fix would require modifying the SAME CDi core to expose the thumbpad as an analog input port (`IPT_AD_STICK_X/Y`) and updating the libretro joystick provider to pass raw axis values through without thresholding. This is something that may be explored as a future contribution to the SAME CDi core.

---

## Building

Requires Visual Studio 2022 or later with the **.NET desktop development** workload.

```
git clone <repo>
cd "Emutastic"
dotnet build
```

No NuGet packages beyond the standard WPF/.NET 8 SDK are required. libretro cores and SDL3 are runtime dependencies only and are not referenced at build time.

---

## Credits

### Controller Illustrations
Controller artwork is sourced from [OpenEmuControllerArt](https://github.com/kodi-game/OpenEmuControllerArt) by the OpenEmu Team, used under the [BSD 3-Clause License](https://opensource.org/licenses/BSD-3-Clause). Copyright © 2013 OpenEmu Team. This project is not affiliated with or endorsed by the OpenEmu Team.

Some additional controller images are from unknown sources — if you are the original artist and would like credit or removal, please open an issue.

| Artist | Controllers |
|---|---|
| **David McLeod** ([@Mucx](https://twitter.com/Mucx/)) | 32X, Famicom Disk System, Game Boy, Game Boy Advance, Game Gear, Master System, NES/Famicom, Sega CD, Sega Genesis/Mega Drive, SNES/Super Famicom |
| **Ricky Romero** ([@RickyRomero](https://twitter.com/RickyRomero/)) | Atari 2600, Atari 5200, Intellivision, Nintendo 64, Nintendo DS, Odyssey², PlayStation, PSP, Sega Saturn, SG-1000, Vectrex, Virtual Boy |
| **Craig Erskine** ([@qrayg](https://twitter.com/qrayg/)) | GameCube, Neo Geo Pocket, PC Engine, PC Engine CD, TurboGrafx-16 |
| **Salvo Zummo** ([@noisymemories](https://twitter.com/noisymemories/)) | Atari 7800 |
| **David Everly** ([@selfproclaim](https://twitter.com/selfproclaim/)) | 3DO |
| **Kate Schroeder** ([@medgno](https://twitter.com/medgno/)) | ColecoVision |
| **Unknown** | Dreamcast |

### Inspiration
This project is inspired by [OpenEmu](https://openemu.org/) for macOS. Emutastic is an independent project and is not affiliated with or endorsed by the OpenEmu team.

---

## License

Emutastic is licensed under the [GNU General Public License v3.0](LICENSE). You are free to use, modify, and distribute this software under the terms of the GPL — any distributed modifications must also be made available under the same license.
