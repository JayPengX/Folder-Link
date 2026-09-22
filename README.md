# FolderLink

Move a folder's contents to a new location and leave a link behind, so
nothing that points at the old path ever notices it moved.

## Table of Contents

- [Overview](#overview)
- [Download](#download)
- [Requirements](#requirements)
- [Getting Started / Usage](#getting-started--usage)
- [How It Works](#how-it-works)
- [Scenarios It Handles](#scenarios-it-handles)
- [Is This Safe?](#is-this-safe)
- [Verifying Your Download](#verifying-your-download)
- [Build It Yourself](#build-it-yourself)
- [Testing](#testing)
- [Legacy PowerShell Version](#legacy-powershell-version)
- [Known Limitations](#known-limitations)

## Overview

FolderLink is a small Windows GUI tool that moves everything inside one
folder to a new location and leaves a link behind at the original path,
so any shortcut, saved setting, or program that still points at the old
location keeps working. This is the same idea as the classic
`robocopy /move` + `mklink` combo, wrapped in a single-file app so you
don't have to type paths in `cmd`.

**The application's own interface is entirely in Traditional Chinese** —
that's a deliberate, permanent property of the shipped app, not a
documentation gap. This README is written in English, but wherever it
references an on-screen button or field, it gives the Chinese label
alongside its English translation (e.g. "Start Transfer (開始搬移)") so
you can match what's written here to what you actually see on screen.

## Download

**[`FolderLink.exe`](https://raw.githubusercontent.com/jaypengx-collab/Folder-Link/claude/windows-file-transfer-symlinks-yt5iiz/FolderLink.exe)**
— one file, no installer, no dependencies to install first (it bundles
its own .NET runtime). Right-click the link → *Save link as...*, or use
the green **Code → Download ZIP** button at the top of this repo to get
the whole source tree instead.

The download is around 70 MB — that's normal for a self-contained .NET
app that carries its own runtime so it works on a bare Windows machine
with nothing preinstalled.

After downloading, verify it against [`checksums.txt`](checksums.txt)
before running it — see [Verifying Your Download](#verifying-your-download)
below.

Don't want to run a prebuilt binary at all? See
[Build It Yourself](#build-it-yourself) — the full C# source is in this
repo and builds with one command.

## Requirements

- Windows 10 or 11, 64-bit. Nothing else needs to be installed — the exe
  is self-contained.
- Administrator rights (needed to create the symbolic link). Windows
  shows the UAC prompt automatically as soon as you launch the app.

## Getting Started / Usage

1. Double-click **`FolderLink.exe`**. When the User Account Control
   (使用者帳戶控制) prompt appears, click **Yes (是)** — the tool needs
   administrator rights to create the link.
2. **Source folder (來源資料夾)** — click **Browse... (瀏覽...)** and
   choose the folder whose *contents* you want to move.
3. **Destination folder (目的資料夾)** — click **Browse... (瀏覽...)**
   and choose (or create) the new location the files should move to.
4. Click **Start Transfer (開始搬移)**, then confirm in the dialog that
   pops up by clicking **Yes (是)**.
5. Watch the log at the bottom of the window until it finishes.

When it finishes, the destination folder holds all the files, and the
original folder path now resolves to that same content through the
link — existing desktop shortcuts, saved paths in other apps, etc. keep
working without any changes on your part.

If a file is in use by another program during the move and can't be
moved, the tool automatically retries it a few times; if it still can't
be moved, the run stops and the log tells you which file was the
problem. Close whatever has that file open and click **Start Transfer
(開始搬移)** again — files already moved won't be touched a second time,
so the retry only deals with what's left.

If Windows shows the blue "Windows protected your PC" SmartScreen
screen, that's expected for a new, unsigned tool and not a sign of an
actual detected threat — click **More info**, then **Run anyway**. See
[Is This Safe?](#is-this-safe) below for the full explanation.

## How It Works

Under the hood the app runs:

```
robocopy "<source>" "<destination>" /MOVE /E /IS /R:5 /W:5 /XJ /MT:8
```

then, once everything copied cleanly, deletes the now-empty source
folder and recreates it as a symbolic link (via .NET's
`Directory.CreateSymbolicLink`) pointing at the destination.

## Scenarios It Handles

- **A file is open/in use** — Robocopy retries a locked file 5 times, 5
  seconds apart, then moves on to the rest instead of stalling forever.
  If a file is still locked afterward, the tool **stops before touching
  the original folder** (no delete, no link) and tells you which run to
  check in the log. Close whatever has the file open and click **Start
  Transfer (開始搬移)** again — files already moved are gone from the
  source, so the second run only deals with what's left; nothing is
  copied twice.
- **Not enough disk space** — checked before anything is touched, using
  the actual size of the source folder vs. free space at the destination
  drive.
- **Destination doesn't exist yet** — created automatically by robocopy
  itself when it copies (also available via the browse dialog's "Make
  New Folder").
- **Destination not empty / already has files** — the transfer still
  runs (robocopy merges), but you're warned in the confirmation dialog
  to double check that's what you want.
- **Source or destination is itself already a link** — refused with an
  explanation, so you can't accidentally link a link.
- **Destination is inside the source (or vice versa)** — refused, since
  that would try to copy a folder into itself.
- **Cancel mid-transfer** — stops robocopy immediately. Files already
  moved stay moved; nothing else is touched, and no link is created, so
  you're never left with a half-empty, half-linked folder.
- **App closed mid-transfer** — you're asked to confirm; confirming kills
  the copy safely, with the same guarantees as Cancel.
- **Link creation fails after a successful move** (e.g. a stray
  permissions issue) — the files are safely at the destination; the tool
  tells you and gives you the manual `mklink` command to finish the last
  step yourself.

The on-screen log at the bottom of the window shows robocopy's progress
for the current run; it isn't saved to disk, so nothing is left behind
once you close the app.

## Is This Safe?

If SmartScreen or Microsoft Defender flags this the first time you run
it, that's expected for **any** new, unsigned tool from a small
publisher — Windows scores files partly by how many people have already
run them without incident, and a fresh app naturally starts at zero. It
is not a sign of an actual detected threat here. A few things that make
that easy to check for yourself:

- **No network access at all.** It never calls out to the internet.
- **No persistence.** It doesn't touch the registry Run keys, Scheduled
  Tasks, or the Startup folder — it only acts on the two folders you
  pick, while the window is open.
- **Admin rights are used for exactly one thing**: creating the symbolic
  link at the end, which Windows requires elevation for.
- **The full source is in this repo** (see [`src/FolderLink`](src/FolderLink))
  and builds into exactly the file you downloaded — see
  [Build It Yourself](#build-it-yourself) if you'd rather compile it
  than trust the prebuilt binary.

If Windows SmartScreen blocks it with "Windows protected your PC": click
**More info**, then **Run anyway**. If Defender quarantines the file,
you can restore it from Windows Security → Virus & threat protection →
Protection history, or download it again after verifying the checksum
below.

## Verifying Your Download

Compare the SHA-256 hash of what you downloaded against
[`checksums.txt`](checksums.txt):

```powershell
Get-FileHash .\FolderLink.exe
```

If the hash doesn't match the one in `checksums.txt`, don't run the
file — re-download it.

## Build It Yourself

The app is plain C# / WinForms, in [`src/FolderLink`](src/FolderLink).
If you have the [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0)
installed (Windows, macOS, or Linux — it cross-compiles):

```
cd src/FolderLink
dotnet publish -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true
```

The resulting `FolderLink.exe` lands in
`src/FolderLink/bin/Release/net8.0-windows/win-x64/publish/`.

If you're building from Linux, note that this project's cross-compile
setup required a few non-obvious csproj settings (`EnableWindowsTargeting`,
a `FrameworkReference` to `Microsoft.WindowsDesktop.App.WindowsForms`
instead of the usual WinForms SDK imports) — see `CLAUDE.md` in the repo
root if you're modifying the build rather than just running it as-is.
Alternatively, `scripts/build.sh` installs the SDK if missing, runs the
test suite, and publishes the exe in one step.

## Testing

The code is split into two parts:

- **[`src/FolderLink.Core`](src/FolderLink.Core)** — plain, OS-agnostic
  logic with no UI and no `robocopy` calls: the pre-flight checks that
  decide whether a move is safe (same/nested folders, an already-linked
  source or destination, disk space) and the recursive size calculation.
  This is the part where a bug could mean lost or corrupted files, so
  it's covered by a real, automated, passing test suite in
  [`src/FolderLink.Core.Tests`](src/FolderLink.Core.Tests), which runs
  on Linux — run it with:
  ```
  cd src/FolderLink.Core.Tests
  dotnet test
  ```
  These tests run on real temporary directories and real symbolic links
  (`Directory.CreateSymbolicLink` is cross-platform in .NET 6+ and works
  fine on Linux for this purpose), and check every refusal case (same
  folder, nested either direction, already a link, disk-space
  arithmetic, a sibling-folder-name-prefix edge case) alongside the
  normal, allowed case. One gap: the disk-space-insufficient refusal
  path has no test, since simulating a full disk without a mock
  `DriveInfo` is impractical with the current code.
- **[`src/FolderLink`](src/FolderLink)** — the WinForms UI, the actual
  `robocopy.exe` invocation and output parsing, admin elevation via the
  app manifest, and the finalize-and-link step. This part is
  Windows-only — robocopy isn't available on Linux and WinForms can't
  render there — so it is **not covered by automated tests**. It has
  been verified by compiling cleanly and by careful manual code review,
  not by actually running it: this project has been developed and
  tested entirely from Linux, and no session so far has had access to a
  real Windows machine. Treat this part as "should work" rather than
  "verified end-to-end," and if you ever get access to real Windows,
  running the actual exe through a real transfer is the one check no
  amount of code review can substitute for.

## Legacy PowerShell Version

An earlier version of this tool shipped as a `.ps1` script + `.bat`
launcher instead of a compiled app. It's kept in
[`legacy-script-version/`](legacy-script-version) for anyone who prefers
an interpreted, directly-readable script over a compiled binary, but
it's no longer the recommended way to get FolderLink — use
`FolderLink.exe` above instead. Note that the legacy script still has an
older Chinese-filename encoding bug that the compiled app has since
fixed (see that folder's own README for details).

## Known Limitations

- Only tested against local NTFS folders and standard file moves; very
  unusual setups (reparse points *inside* the source tree, files with
  paths near Windows' `MAX_PATH` limit, etc.) aren't specially handled
  beyond what `robocopy` itself does.
- No code signing (would require a paid certificate) — see
  [Is This Safe?](#is-this-safe) for why the SmartScreen/Defender
  warning on first run is expected and not a sign of an actual threat.
- No custom app icon — the `.exe` uses the .NET default.
- The exe is large (~69 MB) because it's self-contained (it bundles its
  own .NET runtime so it runs on a bare Windows machine with nothing
  preinstalled). A framework-dependent build would be much smaller but
  would require the .NET 8 Desktop Runtime to already be installed,
  which most Windows PCs don't have out of the box — self-contained was
  chosen deliberately to keep things "just works, no prerequisites."

---

Ready to try it? Jump back up to [Download](#download).
