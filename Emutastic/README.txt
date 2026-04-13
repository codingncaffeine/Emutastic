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


DATA DIRECTORY
--------------
By default all app data is stored in:
  %AppData%\Roaming\Emutastic\

If you use ScreenScraper video snaps, these files (2-6 MB each) can add
up quickly. You can move the entire data directory to another drive in
Preferences → Library → Data Directory. The app will offer to move your
existing files automatically.


BIOS FILES
----------
Place BIOS files in:
  %AppData%\Roaming\Emutastic\System\
  (or wherever your data directory is set)

You can also place them in the same folder as your ROMs for that system.
See Preferences → System Files for the exact filenames required per system.


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
