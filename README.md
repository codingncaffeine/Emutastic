![Emutastic](Emutastic/Assets/banners%20and%20icons/emutastic-banner-scaled.png)

# Emutastic

A multi-system emulator frontend for Windows built with WPF and .NET 8, inspired by [OpenEmu](https://openemu.org/) on macOS. Games are organized by console in a clean library interface. Emulation is handled by [libretro](https://www.libretro.com/) cores loaded at runtime — no cores are bundled.

> **Legal notice:** This project is a frontend application only. It does not include, distribute, or facilitate the acquisition of any copyrighted software, ROM images, BIOS files, or other proprietary system files. You are solely responsible for ensuring you have the legal right to use any software you load into this application. The authors of this project do not condone piracy.

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
| Arcade | Arcade | fbneo *(recommended)* → fbalpha2012 → fbalpha2012_cps1/2/3/neogeo → mame2003_plus → mame2003 → mame2010 → mame2015 → mame2016 → mame → mame2000 | No (game ROMs in ZIP/7z) |
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

## Core Options

Per-core settings (internal resolution, graphics plugins, controller pak type, etc.) are accessible in two places:

- **In-game**: tap the cog icon in the overlay menu → "Core Options…"
- **Preferences → Core Options tab**: browse and edit options for any core without a game running

> **Note:** Core options for a platform only appear after you have launched at least one game for that platform. On first launch, the core's available options are captured automatically — after that they persist and are editable any time.

Options are saved per-core and applied live where the core supports mid-session changes. Some options (e.g. internal resolution on PPSSPP) require a restart to take effect.

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
- mupen64plus_next is listed as a fallback core.

#### Emulation speed / timing pitfall

N64 emulation ran at a locked **48fps on a 144Hz monitor** for a long time despite the core running at full speed. The root cause was using an **audio-driven timing loop** — the loop waited for the audio buffer to drain before calling `retro_run` again. On Windows, `WaveOut` drains its internal buffers in steps synchronized to the DWM compositor rate (144Hz ÷ 3 = 48 drain steps/sec). Each drain step released one frame's worth of budget, capping emulation at 48fps regardless of how fast the CPU was.

The fix is a **Stopwatch-primary loop** for software cores: measure elapsed time with a high-resolution timer, sleep/spin the remainder of the frame budget, and let the audio buffer run slightly ahead. `AudioPlayer.GetBufferedMs()` must also use a time-based estimate (total samples enqueued minus playback time elapsed) rather than reading `BufferedWaveProvider.BufferedBytes`, which only decreases at WaveOut drain cadence.

#### Teardown / close-crash pitfalls

mupen64plus with the glide64 graphics plugin is one of the hardest libretro cores to shut down cleanly. The core uses **libco coroutines** (`co_switch`) and its own internal `EmuThread`, which means cleanup code can fire on unexpected threads and at unexpected times. Here is the full set of issues we hit and how they were resolved — if you are building a libretro frontend that supports N64 with HW rendering, you will likely run into the same problems:

1. **`retro_deinit` must run with a current OpenGL context.** glide64's `retro_deinit` triggers GL cleanup calls (texture deletes, context queries). If no GL context is current on the calling thread, OPENGL32.dll's dispatch table is null → access violation at address `0xA38`. The solution is to call `retro_deinit` on the **emulation thread** (the same OS thread that called `retro_run`) while the GL context is still `wglMakeCurrent`'d — *before* releasing the context. Do **not** defer `retro_deinit` to a background/thread-pool thread; `wglMakeCurrent` will fail on a thread that never owned the context.

2. **Skip `context_destroy` for mupen64plus.** mupen64plus's internal EmuThread continues running cleanup for hundreds of milliseconds after `retro_unload_game` returns (via `co_switch`). Calling the libretro `context_destroy` callback while that thread is still calling GL will crash. Let the quarantine period (below) handle it instead.

3. **`wglDeleteContext` must happen after a delay.** Even after `retro_deinit`, the NVIDIA OpenGL driver may fire background callbacks (texture frees, fence signals) that call back into the core DLL. Deleting the HGLRC immediately causes those callbacks to hit a null dispatch table. Wait ~1.5–2 seconds after `retro_deinit` before calling `wglDeleteContext`.

4. **`FreeLibrary` must happen after `wglDeleteContext`.** `wglDeleteContext` itself triggers NVIDIA driver cleanup that calls back into the core DLL code. If `FreeLibrary` has already unmapped the DLL, those callbacks hit unmapped memory → instant process termination. Call `FreeLibrary` *after* `wglDeleteContext`, not before.

5. **All of the above must complete synchronously before launching another N64 game.** mupen64plus uses global state that is only reset when the DLL is fully unloaded and reloaded. If you fire-and-forget the cleanup (async quarantine), the user can launch a second N64 game before the first instance's DLL is freed → `LoadLibrary` returns the same mapping with stale globals → "Failed to initialize core". Make the cleanup blocking so the DLL is fully unloaded before the frontend considers the window closed.

The correct teardown sequence (on the emu thread, then blocking on a background thread):
```
Emu thread:  retro_unload_game()
             ← skip context_destroy
             retro_deinit()          ← GL context still current
             wglMakeCurrent(NULL)    ← release GL
             thread exits

Background:  join(emu_thread)
             Sleep(1500ms)           ← drain residual driver callbacks
             wglDeleteContext()
             Sleep(500ms)
             FreeLibrary()           ← DLL fully unloaded, safe to relaunch
```

### Dreamcast (Flycast)

#### VMU saves / "No VMU Found"

Getting battery saves to work on Dreamcast requires satisfying five separate conditions simultaneously — missing any one of them results in games reporting "No VMU Found" at the memory card screen:

1. **`RETRO_ENVIRONMENT_SET_CONTROLLER_INFO` (cmd 35) must return `true`.** Returning `false` causes Flycast/Reicast to skip all sub-peripheral initialization entirely, so no VMU ever attaches — even if everything else is correct.

2. **`RETRO_ENVIRONMENT_GET_RUMBLE_INTERFACE` (cmd 23) must supply a function pointer.** Even a no-op stub is fine, but returning `false` also blocks sub-peripheral setup.

3. **Core options `reicast_device_portN_slot1 = "VMU"` must be pre-seeded before `retro_load_game`.** The core reads these during maple bus reconfiguration on load; if they aren't present yet the VMU slots default to empty.

4. **`retro_set_controller_port_device` must be called for all 4 ports**, not just port 0. This triggers a full maple bus reconfiguration that attaches the VMU sub-peripherals for every port.

5. **The `system/dc/` directory must exist before load, but VMU files must NOT be pre-created.** Flycast auto-creates `vmu_save_A1.bin` etc. in that directory using its own zlib-compressed format. If you pre-create the files (e.g. as empty 128KB blobs), the core will see them as corrupt and fail to attach the VMU — back to "No VMU Found".

Note: the core option prefix is `reicast_` in this build, not `flycast_`.

#### 30fps games running at 2× speed

Some Dreamcast games (e.g. Hydro Thunder, Power Stone) run at a native **30fps** — they advance two game frames per `retro_run()` call and produce ~33ms of audio per call. A Stopwatch-primary loop that waits one `targetFrameMs` (16.7ms) between calls will therefore run the game at exactly 2× speed.

The correct timing strategy for HW cores is **audio-drain waiting**: after each `retro_run()`, spin until `GetBufferedMs()` drops back below the pre-fill target (e.g. 150ms). Because the audio buffer drains at real-time rate, waiting for N frames of audio to drain takes exactly N real frame-times — automatically correct for any frame rate. This also naturally handles 60fps games without any special-casing.

```
// HW cores only (Dreamcast, GameCube, N64)
while (audioPlayer.GetBufferedMs() > prefillMs &&
       frameTimer.Elapsed.TotalMilliseconds < targetFrameMs * 4)
    Thread.Sleep(1);
```

The `targetFrameMs * 4` cap handles silent scenes where no audio is produced (otherwise the loop would spin forever).

Software cores (SNES, Genesis, etc.) keep a Stopwatch-primary loop because their audio output is more tightly coupled to the frame rate and the buffer estimate is more predictable.

### Sega CD / Saturn / PS1
- `.cue` files are fully supported across all three systems.
- `.chd` (compressed disc image) is supported — SHA1 is computed from the CHD and matched against DAT files for automatic system detection.
- Region is auto-detected from No-Intro/Redump filename conventions (e.g. `(USA)`, `(Japan)`, `(Europe)`) to select the correct BIOS.

### Vectrex (vecx)
The vecx core uses hardware OpenGL rendering and has some non-obvious requirements for frontends:

- **FBO must be sized before `context_reset`**: The libretro `SET_HW_RENDER` callback fires during `retro_load_game` with no geometry information yet — at that point the frontend can only create a placeholder FBO (e.g. 640×480). The core later reports its true geometry via `retro_get_system_av_info`. If `context_reset` is called while the FBO is still at 640×480, the core queries the FBO dimensions to set up its internal GL viewport and locks onto 640×480 for the session — permanently clipping the top and right edges of every frame. **Fix:** after `retro_load_game` returns, resize the FBO to `max_width × max_height` from `retro_system_av_info` *before* calling `context_reset`.

- **Read the full FBO, not `base_width × base_height`**: vecx renders all game content into the full square FBO (e.g. 1024×1024). The reported `base_width=824` is not the render width — it is used together with `aspect_ratio` (≈0.8049) to tell the frontend to display the square image as portrait. If your frontend reads back only `base_width × base_height` pixels via `glReadPixels`, it will clip the right side of the image. **Fix:** read `fbo_width × fbo_height` and apply the core's `aspect_ratio` to the displayed image.

- **AR correction applies to readback HW cores**: Unlike Dolphin (which renders directly to a Win32 child window), vecx uses `glReadPixels` into a CPU buffer that the frontend displays like a software frame. The aspect ratio correction (scale transform) must be applied the same way as for software cores — not skipped.

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
