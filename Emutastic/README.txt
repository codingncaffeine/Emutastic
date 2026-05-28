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
   - "Update All" updates installed cores to the latest libretro
     nightlies; run it occasionally.

3. If any system requires a BIOS (Sega CD, Saturn, PlayStation, etc.),
   go to Preferences → System Files to see what's needed and where to
   place the files.

4. Drag and drop ROM, disc image, or zip files onto the library window
   to import your games, or use the Import ROMs button in the navigation
   bar below Preferences. Zips are auto-extracted into the data folder;
   the original archive is left untouched.

5. (Optional) Set up artwork and accounts:
   - Preferences -> Snaps: Sign in to ScreenScraper for the richer
     box-art and metadata library used when you're logged in. Without
     an account, Emutastic falls back to an offline metadata source
     (OpenVGDB), which has less complete art coverage.
   - Preferences -> Achievements: Sign in to RetroAchievements
     (see RETROACHIEVEMENTS section below) to track unlocks.
   - Preferences -> Backups: Sign in to GitHub for free cloud sync of
     battery saves and your library database across PCs (see BACKUPS
     section below).


CONTROLLERS
-----------
Connect your controller before launching Emutastic. Button mappings are
configurable in Preferences → Controls. Controllers are detected
automatically — no refresh needed.


KEYBOARD SHORTCUTS
------------------
In the library:
  Ctrl+F     Focus the search box in the active tab
  Ctrl+A     Select all visible games
  Enter      Open the focused game's detail card
  Delete     Remove selected games (save states are preserved) or
             delete selected screenshots
  Esc        Clear the search box and drop focus

In a game (move the mouse to bring up the overlay):
  Print Screen / F12   Take a screenshot
  Esc                  Exit back to the library
  Cog icon             Cheats, save/load state, settings


SCREENSHOTS
-----------
Press Print Screen or F12 in a game to capture the current frame.
Screenshots land in your data folder under Screenshots\<Console>\
and show up in the Screenshots tab in the library sidebar, grouped
per-game. Multi-select with Ctrl-click / Shift-click and press Delete
to remove.


SAVE STATES
-----------
Open the in-game overlay (move the mouse), click the cog, then
"Save State" or "Load State". Existing states are listed in the Save
States tab in the library sidebar, grouped per-game and previewed with
thumbnails. Both tabs have their own search box at the top.

A handful of cores can't create save states reliably and the option is
hidden for them.


THEMES
------
Preferences → Themes switches between bundled themes or loads an
.emutheme file. Click "Edit" on the current theme to open the visual
editor — live color picker, per-console accent / background overrides,
preview as you go. Save edits under a new name; the bundled default
theme is read-only so you always have a known-good fallback.


BACKUPS
-------
Preferences -> Backups has two options for protecting your data:

Local Backup
~~~~~~~~~~~~
Set a folder to back up your library database, battery saves, and
save states. Click "Back Up Now" to copy everything to that folder —
drop it on a USB stick or a cloud-synced folder for safekeeping.
Restore from the same screen. Cores, BIOS files, core options, and
the ROMs themselves are not part of the backup (cores and BIOS are
easy to re-download, ROMs are easy to re-import).

Cloud Sync (GitHub)
~~~~~~~~~~~~~~~~~~~
Sync your battery saves and library database across multiple PCs
using your GitHub account. Sign in once, and a private repo called
"emutastic-saves" is created automatically on your account.

  - Battery saves upload when you close a game
  - The newer save is pulled when you launch a game on another PC
  - "Sync Now" runs a full bidirectional sync of all saves and
    the library database

Optional AES-256-GCM encryption with a passphrase you choose — saves
are encrypted before they leave your machine. The passphrase never
leaves your PC; you'll enter it once per PC.

Save states are NOT included in cloud sync — they get too large for
some consoles (PSP/PS2/GameCube states can be 250 MB+). Use the local
backup option above for save states.

For details on encryption, GitHub storage limits, and sharing saves
with friends, see:
   https://github.com/codingncaffeine/Emutastic/wiki/Cloud-Sync

Local Backup vs Cloud Sync — which to use?
  - Local Backup is one-shot: it copies everything to a folder when
    you click the button. Good for periodic snapshots before a major
    config change.
  - Cloud Sync is continuous: battery saves transfer automatically on
    every game close/launch. Good for keeping multiple PCs in sync.

The two are independent — you can use either, neither, or both.


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

Either trigger works (both opt-in):

  1. Create an empty file named  portable.txt  in the same folder as
     Emutastic.exe, then launch.
  OR
  2. Launch Emutastic with the  --portable  command-line flag:
       Emutastic.exe --portable
     Useful for desktop shortcuts when you don't want to leave a
     marker file in the folder.

From then on, ALL data lives in a  PortableData  subfolder
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


RETROACHIEVEMENTS
-----------------
RetroAchievements (https://retroachievements.org) is a community-run
service that tracks achievement unlocks across hundreds of supported
games. Enable in Preferences -> Achievements:

  1. Username + Password
     Used for the unlocks themselves. After your first successful
     login the password is replaced by a session token, so you only
     enter it once.

  2. Web API Key  (separate from your password)
     Unlocks the per-game stats on the library detail card: an
     achievement progress bar, "Coming up" preview of the achievements
     you're closest to, and "Typical run: beat ~Xh / master ~Yh"
     based on community medians. Without it the unlocks still work,
     just no in-app stats.

     Grab the key from:
        https://retroachievements.org/controlpanel.php
     Sign in, find "Keys" -> "Web API Key" near the bottom, and
     paste the value into Preferences -> Achievements -> Web API Key.

  3. Hardcore Mode  (default ON)
     Disables save state LOADING (creating states is still allowed)
     and cheat codes during gameplay. Required by RA for "hardcore"
     achievement unlocks, which are worth more points and count
     toward the mastery badge.

     Emutastic ships with Hardcore Mode ON to align with RA's
     recommendation for new accounts. Flip the toggle off in
     Preferences -> Achievements -> Hardcore Mode any time if you
     want to use save state loading or cheats — note that any
     achievement unlocks earned with hardcore off won't count toward
     hardcore points or the mastery badge. Switching mid-session is
     not allowed by RA, so the change takes effect on the next game
     launch.

     Hardcore Mode is temporarily disabled for PSP titles regardless
     of the toggle setting — see the Hardcore Compliance wiki page
     for the technical reason.

     For the full line-by-line compliance audit:
        https://github.com/codingncaffeine/Emutastic/wiki/Hardcore-Compliance

In-game, achievements appear as toast notifications the moment you
unlock them. The detail card shows aggregated progress for any game
you've launched at least once with RA enabled.

Friends
~~~~~~~
The Achievements tab has You / Friends sub-tabs that mirror your RA
follow graph. Click a friend to open their detail window: recently-
played games, achievements unlocked, and a compare view for any game
you both own. Following / unfollowing on retroachievements.org is
reflected the next time the tab refreshes.

Leaderboard toasts
~~~~~~~~~~~~~~~~~~
When you place on a leaderboard during play, a toast pops up with your
rank — triumphs, ties, near-misses, and losses each get their own
animation and (optional) sound. Toggle the sound globally with the bell
icon in any Friend Detail window, or disable the toasts entirely in
Preferences → Achievements.


UPDATES
-------
Preferences -> About shows the current version and checks GitHub for
the latest release. If a new version is available, click the "Update"
button to download and install it in-app — Emutastic stages the update
in a temp folder, then a small companion updater replaces the running
binary and relaunches. No manual downloads or file replacements needed.


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

MORE INFORMATION
----------------
GitHub:  https://github.com/codingncaffeine/Emutastic
Website: https://emutastic.com

================================================================================
