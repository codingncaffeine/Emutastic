# Emutastic

A multi-system emulator frontend for Windows built with WPF and .NET 8, inspired by [OpenEmu](https://openemu.org/) on macOS. Games are organized by console in a clean library interface. Emulation is handled by [libretro](https://www.libretro.com/) cores loaded at runtime — no cores are bundled.

> **Legal notice:** This project is a frontend application only. It does not include, distribute, or facilitate the acquisition of any copyrighted software, ROM images, BIOS files, or other proprietary system files. You are solely responsible for ensuring you have the legal right to use any software you load into this application. The authors of this project do not condone piracy.

---

## Requirements

- Windows 10/11 x64
- [.NET 8 Desktop Runtime](https://dotnet.microsoft.com/en-us/download/dotnet/8.0)
- libretro core `.dll` files placed in the `Cores\` folder next to the executable
- `SDL3.dll` (x64) placed next to the executable — used for controller name detection
  - Download from [libsdl.org](https://github.com/libsdl-org/SDL/releases) (Runtime Binaries → Windows x64)

---

## Supported Systems

| System | Console Tag | Core (priority order) | BIOS Required |
|---|---|---|---|
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
| Intellivision | Intellivision | freeintv | `exec.bin` + `grom.bin` (both required) |
| Vectrex | Vectrex | vecx | No |
| 3DO | 3DO | opera | `panafz10.bin` / `panafz1j.bin` / `goldstar.bin` |

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

### Intellivision
Both files are required:
- `exec.bin`
- `grom.bin`

### 3DO
Any one of: `panafz10.bin` (Panasonic), `panafz1j.bin` (Japan), `goldstar.bin` (GoldStar)

### Famicom Disk System
`disksys.rom`

---

## ROM Import

Drag and drop ROM files onto the library window or use **File → Import ROM**. The app:

1. Detects the console from the file extension
2. For ambiguous formats (`.chd`, `.iso`, `.cue`), attempts a SHA1 lookup against [No-Intro / Redump DAT files](https://www.no-intro.org/) placed in the `DATs\` folder — if matched, the console is set automatically
3. If no DAT match is found, shows a console picker so you can assign it manually
4. Hashes the ROM with MD5, cleans the title (strips region/revision tags), and saves it to the library database

### Ambiguous Formats

| Extension | Candidate Systems |
|---|---|
| `.chd` | Sega CD, Saturn, PS1, TurboGrafx-CD, 3DO |
| `.iso` | PSP, GameCube, 3DO |
| `.cue` | Sega CD, Saturn, PS1, TurboGrafx-CD, 3DO |

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
Cores\
    snes9x_libretro.dll
    dolphin_libretro.dll
    opera_libretro.dll
    ... (all other core DLLs)
DATs\
    SegaCD.dat
    Saturn.dat
    PS1.dat
    3DO.dat
    ... (No-Intro / Redump DAT files, named by console tag)
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

### GameCube (Dolphin)
- Dolphin requires the **OpenGL** video backend (`gfx_backend = OGL`). D3D11 and Vulkan are not reliable via the libretro interface on Windows.
- The CPU core must be set to **CachedInterpreter** — JIT64 causes a crash inside .NET's process environment.
- `fastmem` must be **disabled**.
- `AllowHwSharedContext` must be **false** — without this the video output is black.
- Controller port device type must be set after `LoadGame` is called, not before.

### Nintendo 64 (parallel_n64)
- Works correctly when launched from Visual Studio (debug) but may crash outside VS due to `__fastfail` in the dynarec.
- mupen64plus_next is listed as a fallback and is more stable outside a debugger.

### Sega CD / Saturn / PS1
- `.cue` files are fully supported across all three systems.
- `.chd` (compressed disc image) is supported — SHA1 is computed from the CHD and matched against DAT files for automatic system detection.
- Region is auto-detected from No-Intro/Redump filename conventions (e.g. `(USA)`, `(Japan)`, `(Europe)`) to select the correct BIOS.

### PSP (PPSSPP)
- The OpenGL context destruction step is skipped on window close to prevent a crash in the libretro context_destroy callback.

---

## Building

Requires Visual Studio 2022 with the **.NET desktop development** workload.

```
git clone <repo>
cd "Emutastic"
dotnet build
```

No NuGet packages beyond the standard WPF/.NET 8 SDK are required. libretro cores and SDL3 are runtime dependencies only and are not referenced at build time.

---

## Credits

### Controller Illustrations
Controller artwork is primarily sourced from the [OpenEmu](https://github.com/OpenEmu/OpenEmu) project for macOS and used with attribution per their [ILLUSTRATIONS.md](https://github.com/OpenEmu/OpenEmu/blob/master/ILLUSTRATIONS.md). Some additional controller images are from unknown sources — if you are the original artist and would like credit or removal, please open an issue.

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
