================================================================================
 Emutastic — Quick Start Guide
================================================================================

REQUIREMENTS
------------
Visual C++ Redistributable 2022 (x64) — required by emulator cores.
Download: https://aka.ms/vs/17/release/vc_redist.x64.exe

That's it. No other runtime installation needed.


GETTING STARTED
---------------
1. Run Emutastic.exe

2. Open Preferences (gear icon) and go to Cores / Extras:
   - Download the cores for the systems you want to play
   - Download SDL3.dll for controller name detection
   - Download DAT files for automatic disc image detection during import

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
  %AppData%\Roaming\Emutastic\System\

You can also place them in the same folder as your ROMs for that system.
See Preferences → System Files for the exact filenames required per system.


MORE INFORMATION
----------------
GitHub:  https://github.com/codingncaffeine/Emutastic
Website: https://emutastic.com

================================================================================
