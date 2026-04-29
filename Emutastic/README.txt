================================================================================
 Emutastic — Quick Start Guide
================================================================================

REQUIREMENTS
------------
Visual C++ Redistributable 2022 (x64) — required by emulator cores.
Download: https://aka.ms/vs/17/release/vc_redist.x64.exe

That's it. No other runtime installation needed.


WINDOWS SMARTSCREEN
-------------------
Emutastic is not code-signed, so Windows SmartScreen may block the app
on first launch. Click "More info" then "Run anyway" to proceed. This
is normal for unsigned open-source software.


GETTING STARTED
---------------
1. Run Emutastic.exe

2. Open Preferences (gear icon) and go to Cores / Extras:
   - Download the cores for the systems you want to play
   - Download SDL3.dll for controller name detection
   - Download DAT files — these are important! Without them, disc images
     and some cartridge ROMs may be assigned to the wrong system or
     require manual selection during import. Grab all of them.

3. If any system requires a BIOS (Sega CD, Saturn, PlayStation, etc.),
   go to Preferences → System Files to see what's needed and where to
   place the files.

4. Drag and drop ROM or disc image files onto the library window to import
   your games, or use the Import ROMs button in the navigation bar below
   Preferences.


CONTROLLERS
-----------
Connect your controller before launching Emutastic. Button mappings are
configurable in Preferences → Controls. Controllers are detected
automatically — no refresh needed.


BIOS FILES
----------
Place BIOS files in:
  %AppData%\Emutastic\System\
  (or wherever your data directory is set; in portable mode this is
  PortableData\System\ next to Emutastic.exe)

You can also place them in the same folder as your ROMs for that system.
See Preferences → System Files for the exact filenames required per system.


PORTABLE MODE
-------------
Want Emutastic to keep everything (config, library database, saves, save
states, screenshots, artwork) in its own folder instead of %AppData% —
so you can run it from a USB stick, sync the whole folder, or just keep
your install self-contained?

  1. Create an empty file named  portable.txt  in the same folder as
     Emutastic.exe.
  2. Launch Emutastic.

That's it. From then on, all data lives in a  PortableData  subfolder
right next to the .exe. Nothing is written to %AppData%, and the
first-run "choose data folder" prompt is skipped.

To go back to normal mode, simply delete  portable.txt  — your
%AppData% data (if any) becomes active again.

Note: portable.txt must be at the same level as the .exe, not inside a
subfolder. The folder must also be writable (running from a read-only
location like a CD silently falls back to %AppData% mode).


CHEATS
------
Per-game cheats can be managed two ways:

  - In-game: open the overlay (move the mouse), click the cog, and
    choose "Cheats" → "Add Cheat...". Cheats apply immediately when
    "Enable now" is checked.
  - From the library: click a game to open its detail card, then
    "⋯" → "Cheats...". Lets you set up cheats before launching;
    they apply the next time you start the game.

Enter a Title and a Code, decide whether to enable it immediately,
and click Add Cheat.

Existing cheats are listed under Add Cheat... with a checkmark next to
the active ones. Click any existing cheat to edit it, toggle it on/off,
or delete it. Cheats are saved per-game and re-applied automatically
after you load a save state.

Code formats depend on the system:
  Game Genie         — NES, SNES, Game Boy/GBC, Genesis, Master System
  GameShark          — GBA, NDS, N64, PlayStation
  Raw address:value  — PlayStation, TG16, NGP, Virtual Boy, Saturn,
                       Dreamcast, Atari 2600

A few cores cannot apply cheats (PSP, 3DS, Vectrex, 3DO, CD-i, NeoGeo,
ColecoVision, DOS). For those systems the Cheats option is hidden.


CORE SPECIFIC NOTES
-------------------
GameCube (Dolphin): The emulator core remains loaded in memory after
closing a game to prevent a crash during cleanup. This is harmless
and the memory is reclaimed when Emutastic exits.

N64 (parallel_n64): May crash on close due to internal cleanup threads.
This is a known issue with the core and does not affect save data.


MORE INFORMATION
----------------
GitHub:  https://github.com/codingncaffeine/Emutastic
Website: https://emutastic.com

================================================================================
