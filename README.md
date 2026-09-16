# FolderLink

A small Windows GUI tool that moves everything inside one folder to a new
location and leaves a link behind at the original path, so any shortcut,
saved setting, or program that still points at the old location keeps
working. This is the same idea as the classic `robocopy /move` + `mklink`
combo, wrapped in a single-file app so you don't have to type paths in
`cmd`.

**程式介面已完全採用繁體中文**，操作步驟如下：

## 中文使用說明

1. 下載 **`FolderLink.exe`**（單一檔案，不需安裝），直接雙擊執行。
2. 出現「使用者帳戶控制」視窗時，按下**「是」**（本工具需要系統管理員權限才能建立捷徑）。
3. 在「**來源資料夾**」欄位按「**瀏覽...**」，選擇要搬移檔案的資料夾。
4. 在「**目的資料夾**」欄位按「**瀏覽...**」，選擇（或建立）檔案要搬到的新位置。
5. 按下「**開始搬移**」，在跳出的確認視窗按「**是**」。
6. 等待畫面下方的記錄跑完即完成。完成後，原本的資料夾位置會變成一個指向新位置的捷徑，桌面捷徑、其他程式記住的路徑都不需要更動，一樣能正常使用。

若過程中某個檔案正在被其他程式使用而無法搬移，程式會自動重試幾次；如果還是不行，會停下來並在記錄中說明是哪個檔案，把該程式關閉後再按一次「開始搬移」即可，已經搬過的檔案不會被重複處理。

若 Windows 顯示「Windows 已保護您的電腦」的藍色警告畫面，這是因為這是一個新發布、未經付費簽章的小工具，非常正常，並非偵測到病毒。可以點選「**其他資訊**」，再按「**仍要執行**」即可繼續（下方英文版說明中的
[Windows warned me this might be dangerous — is it?](#windows-warned-me-this-might-be-dangerous--is-it)
一節有更詳細的原因說明）。

## Download

**[`FolderLink.exe`](https://raw.githubusercontent.com/jaypengx-collab/Folder-Link/claude/windows-file-transfer-symlinks-yt5iiz/FolderLink.exe)**
— one file, no installer, no dependencies to install first (it bundles its
own .NET runtime). Right-click the link → *Save link as...*, or use the
green **Code → Download ZIP** button at the top of this repo to get the
whole source tree instead.

The download is around 70 MB — that's normal for a self-contained .NET
app that carries its own runtime so it works on a bare Windows machine
with nothing preinstalled.

After downloading, verify it against [`checksums.txt`](checksums.txt)
before running it — see [Verifying your download](#verifying-your-download)
below.

Don't want to run a prebuilt binary at all? See
[Build it yourself](#build-it-yourself) — the full C# source is in this
repo and builds with one command.

## Requirements

- Windows 10 or 11, 64-bit. Nothing else needs to be installed — the exe
  is self-contained.
- Administrator rights (needed to create the symbolic link). Windows
  shows the UAC prompt automatically as soon as you launch the app.

## Usage

The app's UI is in Traditional Chinese (see [中文使用說明](#中文使用說明) above
for the same steps in Chinese).

1. Double-click **`FolderLink.exe`**. Accept the UAC prompt.
2. **來源資料夾 (Source folder)** — Browse to the folder whose *contents*
   you want to move.
3. **目的資料夾 (Destination folder)** — Browse to where the files should
   go. You can create a new folder from the browse dialog.
4. Click **開始搬移 (Start Transfer)**, confirm the summary dialog, and
   watch the log.

When it finishes, the destination folder holds all the files, and the
original folder path now resolves to that same content through the link —
existing desktop shortcuts, saved paths in other apps, etc. keep working.

## How it works

Under the hood the app runs:

```
robocopy "<source>" "<destination>" /MOVE /E /IS /R:5 /W:5 /XJ /MT:8 /UNICODE
```

then, once everything copied cleanly, deletes the now-empty source folder
and recreates it as a symbolic link (via .NET's
`Directory.CreateSymbolicLink`) pointing at the destination.

## Scenarios it handles

- **A file is open/in use** — Robocopy retries a locked file 5 times, 5
  seconds apart, then moves on to the rest instead of stalling forever. If
  a file is still locked afterward, the tool **stops before touching the
  original folder** (no delete, no link) and tells you which run to check
  in the log. Close whatever has the file open and click **開始搬移
  (Start Transfer)** again — files already moved are gone from the
  source, so the second run only deals with what's left; nothing is
  copied twice.
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
- **Non-ASCII (e.g. Chinese) file and folder names** — robocopy is run
  with `/UNICODE` and its output is decoded as UTF-16, so names don't get
  garbled in the log regardless of the system's default codepage.
- **Link creation fails after a successful move** (e.g. a stray
  permissions issue) — the files are safely at the destination; the tool
  tells you and gives you the manual `mklink` command to finish the last
  step yourself.

## Logs

Each run writes robocopy's output to
`%LOCALAPPDATA%\FolderLink\Logs\transfer_<timestamp>.log`. Use **開啟記錄
資料夾 (Open Log Folder)** in the app to jump there — handy if you need to
see exactly which files failed.

## Windows warned me this might be dangerous — is it?

If SmartScreen or Microsoft Defender flags this the first time you run it,
that's expected for **any** new, unsigned tool from a small publisher —
Windows scores files partly by how many people have already run them
without incident, and a fresh app naturally starts at zero. It is not a
sign of an actual detected threat here. A few things that make that easy
to check for yourself:

- **No network access at all.** It never calls out to the internet.
- **No persistence.** It doesn't touch the registry Run keys, Scheduled
  Tasks, or the Startup folder — it only acts on the two folders you pick,
  while the window is open.
- **Admin rights are used for exactly one thing**: creating the symbolic
  link at the end, which Windows requires elevation for.
- **The full source is in this repo** (see [`src/FolderLink`](src/FolderLink))
  and builds into exactly the file you downloaded — see
  [Build it yourself](#build-it-yourself) if you'd rather compile it than
  trust the prebuilt binary.

If Windows SmartScreen blocks it with "Windows protected your PC": click
**More info**, then **Run anyway**. If Defender quarantines the file, you
can restore it from Windows Security → Virus & threat protection →
Protection history, or download it again after verifying the checksum
below.

### Verifying your download

Compare the SHA-256 hash of what you downloaded against
[`checksums.txt`](checksums.txt):

```powershell
Get-FileHash .\FolderLink.exe
```

If the hash doesn't match the one in `checksums.txt`, don't run the
file — re-download it.

## Build it yourself

The app is plain C# / WinForms, in [`src/FolderLink`](src/FolderLink). If
you have the [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0)
installed (Windows, macOS, or Linux — it cross-compiles):

```
cd src/FolderLink
dotnet publish -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true
```

The resulting `FolderLink.exe` lands in `src/FolderLink/bin/Release/net8.0-windows/win-x64/publish/`.

## Testing

The code is split into two parts:

- **[`src/FolderLink.Core`](src/FolderLink.Core)** — plain, OS-agnostic
  logic with no UI and no `robocopy` calls: the pre-flight checks that
  decide whether a move is safe (same/nested folders, an already-linked
  source or destination, disk space) and the recursive size calculation.
  This is the part where a bug could mean lost or corrupted files, so
  it's covered by an automated test suite in
  [`src/FolderLink.Core.Tests`](src/FolderLink.Core.Tests) — run it with:
  ```
  cd src/FolderLink.Core.Tests
  dotnet test
  ```
  These tests run on real temporary directories (including real symbolic
  links) and check every refusal case (same folder, nested either
  direction, already a link, sibling folders that merely share a name
  prefix) alongside the normal, allowed case.
- **[`src/FolderLink`](src/FolderLink)** — the WinForms UI and the actual
  `robocopy` invocation. This part is Windows-only (it needs `robocopy`
  and real file locks to exercise properly) and isn't covered by
  automated tests; it's been verified by compiling cleanly and by manual
  code review, but a real run on Windows is still the final check before
  trusting it with files you care about.

## Older PowerShell version

An earlier version of this tool shipped as a `.ps1` script + `.bat`
launcher instead of a compiled app. It's kept in
[`legacy-script-version/`](legacy-script-version) for anyone who prefers
an interpreted, directly-readable script over a compiled binary, but it's
no longer the recommended way to get FolderLink — use `FolderLink.exe`
above instead.

## Limitations

- Only tested against local NTFS folders and standard file moves; very
  unusual setups (reparse points *inside* the source tree, files with
  paths near Windows' `MAX_PATH` limit, etc.) aren't specially handled
  beyond what `robocopy` itself does.
