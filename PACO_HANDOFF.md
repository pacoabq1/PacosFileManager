# Paco’s File Manager handoff

Continue on `remote-pane-prototype` in `pacoabq1/PacosFileManager`, a fork of Files. The Windows checkout may already have manual edits from earlier patches. Run `git status --short --branch` and inspect the diff before pulling or merging.

## Run on Windows

Open `Files.slnx` in Visual Studio 2026. Enable Windows Developer Mode, select `Files.App`, Debug and x64, then press F5. The prototype pins SDK 10.0.404 with patch roll-forward and disables app AOT in the win-x64 publish profile. The app omits the Satori SDK import for this prototype. Revisit those build changes before release packaging.

## Implemented

- Split panes support stacked and side-by-side layouts. Both have Back, Forward, Up, Refresh, Home and a read-only path. Paco verified the navigation and active outline on Windows.
- The split views have Opus-inspired colors. Dark mode uses a steel-blue bar and near-black surface for the active pane and burgundy for the other pane. Verify the final palette on Windows.
- FTP and FTPS browsing, saved sites, transfers and pane sync build on commit `bce5c58a7`. The Remote panel shows Connected or Disconnected and reports the current filename and transfer speed. It still handles one remote operation at a time.
- Saved sites can be edited or deleted. Passwords stay in Windows PasswordVault. Startup restoration can reuse credentials when exactly one saved profile matches the host, port and protocol. Verify startup, profile editing and deletion on Windows.
- The FluentFTP listing retry reconnects once and uses LIST after the observed internal null reference. Paco reported browsing worked after applying it. Visual Studio can break on the first thrown exception before the retry runs.
- Password entry takes precedence over the global @ folder alias shortcut. Verify with a test character before entering the saved password.

## Continue next

1. Verify the final combined source on Windows. Test Quick Connect, restart with an FTP folder open, edit and delete a disposable saved site, and download a large test file to watch the speed.
2. Drag and drop is drafted on `claude/remote-pane-next-d9z08r` and needs a Windows build and test. Dragging between split panes copies by default (Shift still moves, Ctrl+Shift or Alt still links); drags inside one pane or between columns keep the usual behavior. Files and folders dragged from the Remote panel list download into the pane folder or folder item they are dropped on. Local files and folders dropped on the Remote panel list upload into the remote folder under the pointer, or the listed folder. Both directions ask before replacing a file or merging into a folder, use the panel's Cancel button and progress, skip remote links and local junctions, and reject names Windows can't store. The FTP folder shown inside a pane is not a drop target for this yet.
3. Add a configurable simultaneous transfer limit and a queue showing filename, status, progress and speed per file. Use separate connections for concurrent transfers.
4. Finish the Opus-style workspace with clickable breadcrumbs, Favorites, independent pane tabs, a folder tree that follows the active pane’s location and colors, This PC drive styling and a remote browser that can occupy either pane.
5. Later work includes Tailscale sending, the Android companion, the Paco’s World project entry, saved workspaces and media ratings.

The combined changes were checked in source on Linux. A Windows build and end-to-end test of the combined branch are still needed. Use disposable files for transfer tests and keep real site credentials on the Windows PC.
