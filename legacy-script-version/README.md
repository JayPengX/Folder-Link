# FolderLink — legacy PowerShell version

This is the original PowerShell + `.bat` launcher implementation of
FolderLink, kept here for anyone who'd rather read/run a plain-text
script than trust a compiled binary.

**This is no longer the recommended way to get FolderLink** — see the
[repo root README](../README.md) for the current single-file
`FolderLink.exe`, which has the same features (plus a couple of fixes
this version doesn't have, like decoding robocopy's Unicode output so
non-English file names don't get garbled).

## Usage

1. Double-click `Run-FolderLink.bat`. Accept the UAC prompt.
2. Fill in the source and destination folders and click 開始搬移
   (Start Transfer).

See [`checksums.txt`](checksums.txt) in this folder to verify these two
files before running them.
