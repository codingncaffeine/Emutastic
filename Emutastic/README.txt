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
Run Emutastic from a USB stick, take it between PCs, sync the whole
folder — everything Emutastic needs lives inside the install folder.

  1. Create an empty file named  portable.txt  in the same folder as
     Emutastic.exe.
  2. Launch Emutastic.

That's it. From then on, ALL data lives in a  PortableData  subfolder
right next to the .exe — that includes the library database, configs,
save states, battery saves, screenshots, recordings, artwork, BIOS
files, libretro cores, and ROMs you import. Nothing is written to
%AppData%, and the first-run "choose data folder" prompt is skipped.

True USB portability — what to expect:

  • Move the entire Emutastic folder to a USB stick.
  • Plug the USB into ANY Windows PC; the drive letter doesn't matter
    (it can be E: on one PC and F: on another). Library paths are
    stored relative to PortableData, so they don't break across PCs.
  • ROMs you import are auto-copied into PortableData\Roms\<Console>\
    so they travel with the USB. You don't have to set up a "library
    folder" — portable mode handles it for you.
  • Cores download into PortableData\Cores\, not next to the .exe,
    so the data folder is fully self-contained.

Important — enable portable mode BEFORE importing ROMs:

  ROMs imported while Emutastic is running in normal mode stay at
  their original location, and the database stores the absolute path
  to wherever you grabbed them from. Switching to portable mode
  afterwards does NOT reach back to copy those ROMs into PortableData.

  If you've already been using Emutastic in normal mode and want to
  switch to portable, the cleanest path is: enable portable mode
  first (drop portable.txt, launch once), then re-import your ROM
  folder. The portable launch will copy each ROM into PortableData
  and the library will travel with the USB from there on.

To go back to normal mode, simply delete  portable.txt  — your
%AppData% data (if any) becomes active again. ROMs, saves, and cores
in PortableData stay where they are; you can move them manually if
you want them under %AppData%.

Note: portable.txt must be at the same level as the .exe, not inside a
subfolder. The folder must also be writable (running from a read-only
location like a CD silently falls back to %AppData% mode).


CHEATS
------
Per-game cheats can be managed two ways:

  - In-game: open the overlay (move the mouse), click the cog, and
    choose "Cheats" -> "Add Cheat...". Each cheat has a pill-style
    toggle switch on the left -- click it to flip on/off without
    opening the editor. Click anywhere else on the row to edit.
  - From the library: click a game to open its detail card, then
    "..." -> "Cheats...". Same toggles and editor; changes apply
    the next time you start the game.

Cheats database
~~~~~~~~~~~~~~~
The community libretro cheats database is one click away. Open
Preferences -> Cores / Extras and download "Cheats Database" (about
37 MB, single download covering 25+ systems). After it's installed,
open any game's cheats menu and click "Import from database..." --
matching cheats are imported all-disabled, then you toggle on the
ones you want.

Cheats are matched by ROM filename, so for best results use ROMs that
match the No-Intro / Redump naming convention. Different ROM regions
(USA / Europe / Japan) often have different memory layouts, so an
imported cheat list applies to the matching region's ROM.

Code formats supported:
  Game Genie               -- NES, SNES, Game Boy/GBC, Genesis,
                              Master System
  GameShark                -- GBA, NDS, N64, PlayStation
  Action Replay / raw      -- Genesis, Saturn, others (frontend
                              applies these directly to system RAM
                              every frame, the same way RetroArch
                              does for "RetroArch handled" cheats)

A few cores cannot apply cheats (PSP, 3DS, Vectrex, 3DO, CD-i, NeoGeo,
ColecoVision). For those systems the Cheats option is hidden.


CORE SPECIFIC NOTES
-------------------
GameCube (Dolphin): The emulator core remains loaded in memory after
closing a game to prevent a crash during cleanup. This is harmless
and the memory is reclaimed when Emutastic exits.

GameCube on AMD / Intel GPUs: If GameCube games render only in the
bottom-left corner of the window, open Preferences -> Cores / Extras
and enable "GameCube: render to default framebuffer (AMD/Intel GPU
compatibility)" under the Compatibility section. NVIDIA users should
leave it off -- the option is for AMD Radeon and Intel GL drivers
that don't tolerate the default framebuffer indirection. While this
is enabled the in-game overlay (cog menu, save/load, cheats panel)
is hidden for GameCube, but the game itself will render correctly.

N64 (parallel_n64): May crash on close due to internal cleanup threads.
This is a known issue with the core and does not affect save data.


MORE INFORMATION
----------------
GitHub:  https://github.com/codingncaffeine/Emutastic
Website: https://emutastic.com

================================================================================
