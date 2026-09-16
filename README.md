# FolderLink

A small Windows GUI tool that moves everything inside one folder to a new
location and leaves a link behind at the original path, so any shortcut,
saved setting, or program that still points at the old location keeps
working. This is the same idea as the classic `robocopy /move` + `mklink`
combo, just wrapped in a UI so you don't have to type paths in `cmd`.

## Download

- **[Download ZIP](https://github.com/jaypengx-collab/Folder-Link/archive/refs/heads/claude/windows-file-transfer-symlinks-yt5iiz.zip)**
  — everything in one file, or use the green **Code → Download ZIP**
  button at the top of this repo.
- Or grab the two files individually:
  [`FolderLink.ps1`](https://raw.githubusercontent.com/jaypengx-collab/Folder-Link/claude/windows-file-transfer-symlinks-yt5iiz/FolderLink.ps1) +
  [`Run-FolderLink.bat`](https://raw.githubusercontent.com/jaypengx-collab/Folder-Link/claude/windows-file-transfer-symlinks-yt5iiz/Run-FolderLink.bat)
  (right-click each link → *Save link as...*, keep both in the same
  folder).

After downloading, verify the files against [`checksums.txt`](checksums.txt)
before running them — see [Verifying your download](#verifying-your-download) below.

## Requirements

- Windows 10/11 (uses `robocopy`, `mklink`-equivalent `New-Item -ItemType
  SymbolicLink`, and Windows PowerShell 5.1, which ship with Windows).
- Administrator rights (needed to create symbolic links). The tool
  self-elevates and shows a UAC prompt when started.

## Usage

1. Double-click **`Run-FolderLink.bat`** (or right-click `FolderLink.ps1` →
   *Run with PowerShell*). Accept the UAC prompt.
2. **Source folder** — Browse to the folder whose *contents* you want to
   move.
3. **Destination folder** — Browse to where the files should go. You can
   create a new folder from the browse dialog.
4. Click **Start Transfer**, confirm the summary dialog, and watch the log.

When it finishes, the destination folder holds all the files, and the
original folder path now resolves to that same content through the link —
existing desktop shortcuts, saved paths in other apps, etc. keep working.

## How it works

Under the hood the tool runs:

```
robocopy "<source>" "<destination>" /MOVE /E /IS /R:5 /W:5 /XJ /MT:8
```

then, once everything copied cleanly, deletes the now-empty source folder
and recreates it as a symbolic link pointing at the destination.

## Scenarios it handles

- **A file is open/in use** — Robocopy retries a locked file 5 times, 5
  seconds apart, then moves on to the rest instead of stalling forever. If
  a file is still locked afterward, the tool **stops before touching the
  original folder** (no delete, no link) and tells you which run to check
  in the log. Close whatever has the file open and click **Start
  Transfer** again — files already moved are gone from the source, so the
  second run only deals with what's left; nothing is copied twice.
- **Not enough disk space** — checked before anything is touched, using
  the actual size of the source folder vs. free space at the destination
  drive.
- **Destination doesn't exist yet** — created automatically (also via the
  browse dialog's "Make New Folder").
- **Destination not empty / already has files** — the transfer still runs
  (robocopy merges), but you're warned in the confirmation dialog to
  double check that's what you want.
- **Source or destination is itself already a link** — refused with an
  explanation, so you can't accidentally link a link.
- **Destination is inside the source (or vice versa)** — refused, since
  that would try to copy a folder into itself.
- **Cancel mid-transfer** — stops robocopy immediately. Files already
  moved stay moved; nothing else is touched, and no link is created, so
  you're never left with a half-empty, half-linked folder.
- **App closed mid-transfer** — you're asked to confirm; confirming kills
  the copy safely, same guarantees as Cancel.
- **Link creation fails after a successful move** (e.g. a stray
  permissions issue) — the files are safely at the destination; the tool
  tells you and gives you the manual `mklink` command to finish the last
  step yourself.

## Logs

Each run writes robocopy's raw output to
`%LOCALAPPDATA%\FolderLink\Logs\transfer_<timestamp>.out.log` (and a
`.err.log` for anything sent to stderr). Use **Open Log Folder** in the
app to jump there — handy if you need to see exactly which files failed.

## Windows warned me this might be dangerous — is it?

If SmartScreen or Microsoft Defender flags this the first time you run it,
that's expected for **any** new, unsigned tool from a small publisher —
Windows scores files partly by how many people have already run them
without incident, and a fresh script naturally starts at zero. It is not a
sign of an actual detected threat here. A few things that make that easy
to check for yourself:

- **The source is plain, readable PowerShell** — nothing obfuscated,
  Base64-encoded, or downloaded and run at runtime. Open `FolderLink.ps1`
  in Notepad and read it top to bottom; every action it can take is right
  there.
- **No network access at all.** It never calls out to the internet.
- **No persistence.** It doesn't touch the registry Run keys, Scheduled
  Tasks, or the Startup folder — it only acts on the two folders you pick,
  while the window is open.
- **Admin rights are used for exactly one thing**: creating the symbolic
  link at the end, which Windows requires elevation for.

If Windows SmartScreen blocks the `.bat` with "Windows protected your PC":
click **More info**, then **Run anyway**. If Defender quarantines a file,
you can restore it from Windows Security → Virus & threat protection →
Protection history, or download it again after verifying the checksum
below.

### Verifying your download

Compare the SHA-256 hash of what you downloaded against
[`checksums.txt`](checksums.txt):

```powershell
Get-FileHash .\FolderLink.ps1
Get-FileHash .\Run-FolderLink.bat
```

If the hashes don't match the ones in `checksums.txt`, don't run the
files — re-download them.

## Limitations

- Only tested against local NTFS folders and standard file moves; very
  unusual setups (reparse points *inside* the source tree, files with
  paths near Windows' `MAX_PATH` limit, etc.) aren't specially handled
  beyond what `robocopy` itself does.
