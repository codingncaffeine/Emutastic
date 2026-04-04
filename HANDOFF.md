# Emutastic — Session Handoff
**Last updated:** 2026-04-04
**Project:** WPF/.NET 8 multi-system emulator frontend using libretro cores
**Solution root:** `C:\Users\gamer\source\repos\Emutastic\`
**Main project:** `Emutastic\Emutastic\`
**GitHub:** https://github.com/codingncaffeine/Emutastic (private)
**Namespace:** `Emutastic` (previously `OpenEmu_for_Windows`)

---

## What This Project Is

A Windows clone of OpenEmu (macOS). It provides a game library browser (WPF UI) and launches libretro cores to emulate 25+ consoles. The app is a frontend only — all emulation is done by libretro core DLLs in the `Cores\` folder.

---

## Project Structure

```
Emutastic\
├── Emutastic\                    ← main project
│   ├── App.xaml.cs               ← logging, config init
│   ├── MainWindow.xaml.cs        ← game library UI, navigation, launching
│   ├── Views\
│   │   ├── EmulatorWindow.xaml.cs     ← emulation loop, libretro callbacks, GL context
│   │   ├── GameDetailWindow.xaml.cs   ← metadata editor + play button
│   │   ├── CorePreferencesView.xaml.cs
│   │   ├── PreferencesWindow.xaml.cs  ← controller mapping
│   │   └── BiosRequiredWindow.xaml.cs
│   ├── Services\
│   │   ├── LibretroCore.cs       ← P/Invoke wrapper for libretro DLL API
│   │   ├── CoreManager.cs        ← maps consoles → core DLL names, BIOS checking
│   │   ├── ControllerManager.cs  ← XInput polling, button/analog state
│   │   ├── AudioPlayer.cs        ← NAudio output from core audio callbacks
│   │   ├── DatabaseService.cs    ← SQLite game library
│   │   ├── ImportService.cs      ← ROM import, MD5 hashing, artwork fetch
│   │   ├── RomService.cs         ← extension → console mapping
│   │   ├── ArtworkService.cs     ← cover art cache
│   │   ├── VulkanRenderer.cs     ← Vulkan device setup (not fully wired yet)
│   │   └── ConsoleHandlers\      ← *** NEW 2026-03-26 ***
│   │       ├── IConsoleHandler.cs
│   │       ├── ConsoleHandlerBase.cs
│   │       ├── ConsoleHandlerFactory.cs
│   │       ├── GenericHandler.cs
│   │       ├── NesHandler.cs
│   │       ├── SnesHandler.cs
│   │       ├── N64Handler.cs
│   │       ├── Tg16Handler.cs
│   │       └── GameCubeHandler.cs
│   ├── Configuration\
│   │   ├── ConfigurationModels.cs
│   │   ├── IConfigurationService.cs
│   │   └── JsonConfigurationService.cs
│   ├── Models\Game.cs
│   ├── ViewModels\MainViewModel.cs
│   └── Cores\                    ← libretro DLL files (copied at build time)
```

---

## Architecture — How a Game Launches

1. User clicks Play in `GameDetailWindow`
2. `CoreManager.GetCorePath(console)` → resolves DLL path (respects user preferred core)
3. `CoreManager.GetMissingBios()` → if required BIOS missing, shows `BiosRequiredWindow`
4. `new EmulatorWindow(game, new LibretroCore(corePath))` → `ShowDialog()`
5. `EmulatorWindow` constructor:
   - Creates `_consoleHandler = ConsoleHandlerFactory.Create(game.Console)`
   - Seeds core options from handler
   - Pins all libretro callback delegates (GCHandle.Normal to prevent GC)
6. `OnWindowLoaded` → spawns STA thread (32 MB stack) → `StartEmulator()`
7. `StartEmulator()`:
   - Installs VEH crash diagnostics
   - `_core.SetCallbacks(...)` → `_core.Init()` → `_core.LoadGame(romPath)`
   - `_consoleHandler.ConfigureControllerPorts(_core)`
   - Reinitializes `AudioPlayer` with sample rate from core's AV info
   - Enters `EmulationLoop(fps)` — busy-wait calling `_core.Run()` at target FPS
8. During `Init()/LoadGame()`, the core fires `retro_environment_t` callbacks:
   - `SET_HW_RENDER` → `InitOpenGLContext()` → creates HwndHost, DC, GL context 3.3 core → calls `context_reset`
   - `SET_VARIABLES` → seeds `_coreOptions` dict; handler's `OnVariableAnnounced()` runs per key
   - `GET_VARIABLE` → returns from `_coreOptions` dict

---

## Console Handler System (added 2026-03-26)

**Problem solved:** All console-specific code was in `EmulatorWindow.xaml.cs`. Working on GameCube was causing regressions in N64/SNES.

**How it works:** `IConsoleHandler` abstracts all console-specific behaviour. `EmulatorWindow` is now fully generic. `ConsoleHandlerFactory.Create(consoleName)` returns the right handler.

**Interface methods:**
- `GetDefaultCoreOptions()` — options pre-seeded before core announces its variable list
- `ConfigureControllerPorts(core)` — called after `LoadGame` (GameCube needs all 4 ports)
- `OnVariableAnnounced(key, validValues, coreOptions)` — called per-key in `SET_VARIABLES` (GameCube uses this for `dolphin_cpu_core` auto-select)
- `GetDisplayAspectRatio(w, h, coreAr)` — TG16 forces 4:3; others return core value
- `OnBeforeContextReset()` / `OnAfterContextReset()` — GameCube blocks/unblocks D3D11 here

**To add a new console with custom behaviour:** Create `[Console]Handler.cs` in `Services/ConsoleHandlers/`, add a case to `ConsoleHandlerFactory`.

---

## GameCube / Dolphin — Current Status (UNRESOLVED — active work area)

This is the main focus. We have NO video output yet.

### What is known / confirmed working:
- Core loads (`retro_load_game` returns true)
- `SET_HW_RENDER` fires → OpenGL 3.3 context created on HwndHost win32 window
- `context_reset` called successfully
- D3D11 patched to E_FAIL during `context_reset` so Dolphin goes straight to OGL
- `SET_HW_SHARED_CONTEXT` returns true (required — Dolphin's EmuThread needs shared context)
- All 4 controller ports configured (required for Dolphin input subsystem init)
- `dolphin_cpu_core` auto-selects CachedInterpreter (avoids JIT64 which crashes in .NET)
- `dolphin_fastmem` = disabled (no VEH MMIO tricks)
- `dolphin_main_cpu_thread` = disabled (single-threaded, CPU on retro_run thread)
- `dolphin_gfx_backend` = OGL

### Required options (MUST be string values, not numeric):
```
dolphin_gfx_backend     = "OGL"       (not "0" or "opengl")
dolphin_fastmem         = "disabled"
dolphin_main_cpu_thread = "disabled"
dolphin_cpu_core        = auto-selected at runtime (CachedInterpreter preferred)
dolphin_dsp_hle         = "enabled"
dolphin_skip_gc_bios    = "enabled"
```

### Suspected remaining issues:
- `OnVideoRefresh` receives `data == (IntPtr)(-1)` (RETRO_HW_FRAME_BUFFER_VALID) which means Dolphin rendered to FBO 0 (the window backbuffer). We call `SwapBuffers(_hdc)` but nothing appears.
- The HwndHost child window may not be visible / sized correctly during rendering
- Dolphin's Video thread may be creating its own shared GL context (via `wglCreateContextAttribsARB` with `_hglrc` as share) — if that context isn't current on Dolphin's thread when it renders, frames are lost
- `GetCurrentFramebuffer()` returns 0 — correct for FBO 0 (backbuffer), but need to confirm Dolphin expects this

### Architecture of GL setup:
```
_glHwnd        = Win32 HWND (child of HwndHost, embedded in WPF GameViewport grid)
_hdc           = GetDC(_glHwnd)
_hglrc         = main GL context (wglCreateContextAttribsARB, 3.3 core profile)
                 → made current on EmuThread after context_reset
                 → stays current through retro_run (single-threaded mode)
```

### Things NOT tried yet (next steps for GameCube debug):
1. Verify the HwndHost window is actually visible and has non-zero size during rendering
2. Log what Dolphin's video thread does — does it create a shared context? Does it call SwapBuffers itself?
3. Try returning a valid FBO id from `GetCurrentFramebuffer()` instead of 0
4. Confirm `SwapBuffers` is being called on the right HDC (the one Dolphin rendered to)
5. Check if `dolphin_main_cpu_thread = enabled` might work better (Dolphin's own threading)

---

## N64 / Parallel N64 — LOADS AND RENDERS, BUT GAMEPLAY INACCURATE (open issue)

Uses software rendering (angrylion). Loads and renders frames. Black screen was fixed. But **games are not playable** — emulation feels inaccurate/off. Root cause not yet found.

**Bug fixes already applied (not the remaining issue):**
1. `GET_PREFERRED_HW_RENDER` (env cmd 61): Was returning OpenGL Core for all cores — fixed; N64Handler returns -1 (don't handle), parallel-n64 stays software/angrylion.
2. `SET_HW_SHARED_CONTEXT` (env cmd 45): Was returning true for all cores — fixed; only GameCubeHandler returns true.

**Key options:**
```
parallel-n64-gfxplugin  = "angrylion"        (software, safest)
parallel-n64-cpucore    = "dynamic_recompiler"
```
No HW render — uses software video callback path (WriteableBitmap in WPF).

**Timing approaches tried — neither fixed gameplay accuracy:**

1. **CPU-side tick timing** (Stopwatch + timeBeginPeriod(1) + SpinWait): Called `_core.Run()` at exactly 60fps using `Stopwatch.ElapsedTicks`. Resulted in emulation clock drifting from audio clock over time.

2. **Audio-driven sync** (current code as of end of session): `QueueBatch` in `AudioPlayer` blocks when buffer exceeds 100ms. `EmulationLoop` runs `_core.Run()` in a tight loop with no CPU-side frame cap — audio blocking provides the throttle. This is the RetroArch audio-sync approach. Still not playable.

**What to investigate next session:**

- **Frames without audio**: parallel-n64 may not call `OnAudioSampleBatch` on every `retro_run`. If some frames produce no audio, the loop runs those unconstrained (no throttle at all). Need to add a fallback frame cap ceiling in `EmulationLoop` even in audio-sync mode — e.g., never run faster than 2× target fps.
- **`parallel-n64-framerate = "original"`**: Verify this is correct. "original" means the core reports actual N64 framerate. Try `"fullspeed"` to see if it changes behavior.
- **`parallel-n64-audio-buffer-size = "2048"`**: This is the N64 internal audio buffer. Try `"1024"` (less buffering inside core, tighter sync) or `"4096"` (more headroom).
- **`TargetBufferMs` in AudioPlayer**: Currently 100ms. Try 50ms for tighter sync. Lower = less latency, higher = more stable.
- **Add diagnostic logging**: Print how many audio samples the core produces per `retro_run` call. Should be ~735 for 44100Hz/60fps. If wildly different, the core's audio clock is the problem.
- **Thread stack**: Already on 32MB stack STA thread. Probably not the issue.
- **The user says this was fixed before** when initially building the app — worth checking git history / JSONL transcript at `C:\Users\gamer\.claude\projects\C--WINDOWS-system32\7140131b-b959-4e2a-b1e4-8aeb29a177eb.jsonl` for what the original fix was.

**Current code state (end of session):**
- `AudioPlayer.QueueBatch`: blocking write, blocks on `Thread.Sleep(1)` when `BufferedBytes >= targetBytes` (100ms worth)
- `EmulationLoop`: no CPU frame timer, pure tight loop, `timeBeginPeriod(1)` active
- `AudioPlayer`: `BufferDuration = 500ms`, `DiscardOnBufferOverflow = false`, `DesiredLatency = 80ms`

---

## SNES / snes9x — 40–60 fps, Active Work Area (as of 2026-03-31)

**Core:** `snes9x_libretro.dll` (v1.63). Software renderer. Reports 60.099 fps, 32040 Hz, 256×224.

### What is working:
- Loads, runs, renders frames, audio plays, controller input works
- Reaching 40–60 fps in Debug mode — touches full speed briefly

### Root cause of the original 10 fps bug (FIXED 2026-03-31):
`Debug.WriteLine` was being called inside two hot-path libretro callbacks:
1. `OnEnvironment` — had a log line at the top that fired for **every** env command, including `GET_VARIABLE_UPDATE` which snes9x calls every single frame from inside `retro_run`
2. `OnVideoRefresh` — had a log line on every frame

With VS debugger attached, `OutputDebugString` (used by `Debug.WriteLine`) blocks until VS consumes the message — easily 40ms per call. Two calls per frame = ~80–92ms per `retro_run`. Both lines were removed.

### Current emulation loop architecture (audio-driven timing):
The Stopwatch + `Thread.Sleep(1)` frame timer was replaced with audio buffer–driven throttling. The audio hardware (WaveOut) drains `BufferedWaveProvider` at exactly 32040 Hz. `EmulationLoop` calls `retro_run` whenever `_audioPlayer.GetBufferedMs() < 150`, and `SpinWait(100)` otherwise. No `Sleep()`, no frame timer arithmetic. This is the same strategy RetroArch uses — the audio clock IS the frame clock.

```csharp
// EmulationLoop (EmulatorWindow.xaml.cs)
while (running) {
    if (_audioPlayer.GetBufferedMs() < 150)
        _core.Run();   // produces ~16.6ms of audio at SNES rate
    else
        Thread.SpinWait(100);
}
```

**Key parameters:**
- `AudioPlayer.BufferDuration` = 500ms (plenty of headroom)
- `DiscardOnBufferOverflow` = true (drops samples if full, never blocks)
- `WaveOutEvent.DesiredLatency` = 40ms
- `targetBufferMs` = 150ms in `EmulationLoop`
- `AudioPlayer.GetBufferedMs()` = `BufferedBytes * 1000 / (sampleRate * 2 * 2)`

### Remaining FPS dip (~40fps instead of 60fps in Debug mode):
Not yet diagnosed. Likely candidates:
- **Debug mode JIT overhead** — run Release build to verify; if it hits 60fps in Release, it's just JIT cost
- **`_videoPending` flag blocking video updates** — `Dispatcher.BeginInvoke(DispatcherPriority.Render)` for the WPF WriteableBitmap pipeline may be slow in debug; this drops frames but shouldn't reduce the `_frameCount` since that's incremented regardless
- **`new byte[]` allocation in `OnAudioSampleBatch`** — allocates ~2132 bytes every frame; small but in debug mode can add GC pressure
- **Audio buffer calibration** — `targetBufferMs = 150` means we sometimes run slightly ahead; tuning down to 100ms might improve responsiveness slightly

### bsnes (`bsnes_libretro.dll`) — crashes with stack overflow during gameplay (DO NOT USE)

bsnes v115 libretro was tested. It loads and runs at similar FPS to snes9x but crashes after a few seconds of gameplay with exit code `0x80131506` (`COR_E_STACKOVERFLOW`). All threads terminate with this code — .NET kills the entire process due to a fatal unrecoverable stack overflow.

**Root cause:** bsnes has a highly recursive DSP/audio subsystem (cycle-accurate SNES audio processing). The recursion depth overflows the native stack during gameplay (triggered by complex audio scenes like Super Mario World music). This exception cannot be caught in .NET and kills the process.

**Recommendation:** Stay with snes9x for SNES emulation. bsnes would need either a separate process (`Process.Start`) or a much larger stack (100MB+) and is not worth pursuing until snes9x is fully solid.

### To try next for snes9x:
1. Run in Release mode first — if 60fps, the issue is purely debug JIT overhead and not worth fixing
2. If still below 60fps in Release: reduce `targetBufferMs` to 80–100ms
3. Cache the `byte[]` in `OnAudioSampleBatch` (reuse a pre-allocated buffer) to reduce GC

---

## Input System

- `ControllerManager` polls XInput at 60 Hz
- `OnInputState` callback queries `ControllerManager.GetButtonState(id)` for joypad, `GetAnalogAxisValue(index, id)` for analog
- Y-axis is negated in `OnInputState` (XInput up = +32767, libretro up = -32768)
- Keyboard fallback when no controller: WASD = left stick (analog consoles), arrow keys = D-pad, IJKL = right stick
- `_consoleHandler.UsesAnalogStick` determines whether WASD drives analog stick or D-pad

---

## BIOS Files

Stored in: `%APPDATA%\OpenEmuWindows\System\`

Required consoles: FDS (`disksys.rom`), PS1 (`scph*.bin`), Saturn, 3DO, TGCD

---

## Save States

Stored in: `%APPDATA%\OpenEmuWindows\SaveStates\{gameId}\slot{n}.state`

F1/F2 = save/load slot 1, F3/F4 = slot 2

---

## Build

```
dotnet build
```
78 pre-existing warnings (nullable, unused Vulkan fields), 0 errors.
The project targets `net8.0-windows`.

---

## Key Technical Notes

- **GCHandle pinning:** All libretro callback delegates must be kept alive with `GCHandle.Alloc(..., GCHandleType.Normal)`. If GC moves/collects them the core will crash with an access violation.
- **VEH handler:** Installed on every emulation start. Catches access violations, identifies the faulting module, and attempts a NULL pointer fixup for low-address reads/writes.
- **D3D11 blocking (GameCube only):** Patches `D3D11CreateDevice` in-memory to return `E_FAIL` during `context_reset`. Restored immediately after. Lives in `GameCubeHandler`.
- **`NativeMethods2`:** `internal static class` at the bottom of `EmulatorWindow.xaml.cs` (namespace `Emutastic.Views`). Has `LoadLibrary`, `GetProcAddress`, `VirtualProtect`. `GameCubeHandler` has its own duplicate p/invoke declarations to avoid a Views→Services dependency.
- **Libretro option values must be strings**, not numerics — confirmed hard way with Dolphin.

---

## Session History (brief)

| Date | Work |
|------|------|
| Before 2026-03-26 | Built core app: game library, import, N64/SNES working, Dolphin integration attempts |
| 2026-03-26 | Console handler segregation — moved all console-specific code out of EmulatorWindow into `Services/ConsoleHandlers/`. Fixed N64 black screen (GET_PREFERRED_HW_RENDER + SET_HW_SHARED_CONTEXT were leaking GameCube behavior to all cores). Real FPS measurement added. Tick-based emulation loop + timeBeginPeriod(1). N64 confirmed working. GameCube video still unresolved. |
| 2026-03-31 | SNES fixed from ~10fps to 40–60fps. Root cause: `Debug.WriteLine` inside `OnEnvironment` and `OnVideoRefresh` was blocking ~80ms per frame (VS debugger `OutputDebugString` overhead). Both per-frame log lines removed. Replaced Stopwatch+Sleep emulation loop with audio-driven timing: `retro_run` fires when `bufferedAudioMs < 150`, WaveOut clock provides the throttle. `AudioPlayer.GetBufferedMs()` added. Still dips below 60fps — likely debug mode JIT overhead; Release mode untested. bsnes tested — crashes fatal `COR_E_STACKOVERFLOW` (0x80131506) during gameplay due to bsnes' recursive DSP. Not fixable without separate process. Stick with snes9x. |
