# Notes for the next AI agent working on this repo

This file is a handoff note, not user-facing documentation (that's
README.md). It exists because getting this repo's build working involved
several non-obvious discoveries that cost real time to work out — don't
re-derive them from scratch, and don't undo them without understanding
why they're there first.

## What this project is

A Windows GUI tool (`FolderLink.exe`) that moves a folder's contents to a
new location and leaves a symbolic link at the original path. UI is
Traditional Chinese. See README.md for the user-facing story.

History, roughly: started as a PowerShell + `.bat` script
(`legacy-script-version/`), got localized to Traditional Chinese, then
was rewritten as a compiled C#/WinForms app (`src/FolderLink`) because
the user wanted "one application instead of a folder" of files. The
safety-critical logic was later split into `src/FolderLink.Core` (plain
.NET, no Windows dependency) specifically so it could have real automated
tests (`src/FolderLink.Core.Tests`) — that split is deliberate, don't
merge it back into the WinForms project.

## This has been developed and tested entirely from Linux, never Windows

No session so far has had access to an actual Windows machine. That
shaped everything below. If you get access to real Windows, running the
actual `FolderLink.exe` end-to-end (click through the UI, let it move
real files, confirm the link works) would be the single most valuable
thing you could do — it's the one thing no amount of code review from
Linux can substitute for.

## Rebuilding: use `scripts/build.sh`

```
./scripts/build.sh
```

Installs the .NET 8 SDK if missing (`apt-get install -y dotnet-sdk-8.0`
— that's the exact package name that worked; `dotnet-sdk` alone doesn't
exist as an apt package), runs the cross-platform test suite, publishes
the win-x64 self-contained single-file exe, copies it to the repo root,
and rewrites `checksums.txt`. It does **not** commit — you still need to
`git add` and commit yourself.

### Why cross-compiling a WinForms app from Linux needed non-obvious flags

`src/FolderLink/FolderLink.csproj` targets `net8.0-windows` with WinForms,
built on a Linux box with no Windows SDK components installed. Two things
were required to make this work, found by trial and error:

1. **`<EnableWindowsTargeting>true</EnableWindowsTargeting>`** in the
   csproj. Without it, `dotnet build` fails with `NETSDK1100: To build a
   project targeting Windows on this operating system, set the
   EnableWindowsTargeting property to true.` This is the actual
   documented escape hatch — use it, don't fight it.
2. **`<FrameworkReference Include="Microsoft.WindowsDesktop.App.WindowsForms" />`**
   instead of `<Project Sdk="Microsoft.NET.Sdk.WindowsDesktop">` or
   `<UseWindowsForms>true</UseWindowsForms>` under plain
   `Microsoft.NET.Sdk`. Both of those alternatives fail on this Linux
   SDK install: `UseWindowsForms` triggers an `<Import>` of
   `Microsoft.NET.Sdk.WindowsDesktop.targets` by a **hardcoded local
   path** relative to the SDK install, which doesn't exist on Linux (this
   is not resolved via NuGet, so a `global.json` `msbuild-sdks` pin does
   **not** fix it). Using `Sdk="Microsoft.NET.Sdk.WindowsDesktop"`
   directly does go through NuGet SDK resolution, but the only
   `Microsoft.NET.Sdk.WindowsDesktop` package actually published on
   nuget.org is an old, unrelated `3.0.0` — there is no modern version to
   resolve. The `FrameworkReference` approach sidesteps both problems: it
   just pulls the `Microsoft.WindowsDesktop.App.Ref` reference-assembly
   NuGet package (which *is* published for exactly this cross-targeting
   scenario) without needing the SDK's build-logic package at all.
3. `ImplicitUsings` does **not** cover `System.Windows.Forms` or
   `System.Drawing` — add those `using` directives explicitly, plain
   `dotnet build` will fail with CS0246 otherwise.

If a future .NET SDK version changes any of this, the error messages are
usually specific enough to point at the fix (that's how all three of the
above were found) — read them rather than assuming the old workaround
still applies.

### The published exe's hash changes on every rebuild, even with no source changes

`PublishSingleFile` bundling embeds a timestamp/GUID that differs per
build. This means **`checksums.txt` only proves "this file matches what
was committed," not "rebuilding reproduces the same bytes."** Don't be
alarmed if `scripts/build.sh` produces a different hash than what's
currently in `checksums.txt` after a no-op rebuild — that's expected.
Only worry if the *behavior* differs, or if you intended a code change
and the hash *doesn't* change.

## Test coverage: what's real, what isn't

- `src/FolderLink.Core.Tests` (`dotnet test`) — **real, passing,
  automated**, runs on Linux. Covers the pre-flight refusal logic (same
  folder, nested either direction, already-a-link source/destination,
  disk space arithmetic, a sibling-folder-name-prefix edge case) and the
  recursive directory-size calculation, using real temp directories and
  real symbolic links (`Directory.CreateSymbolicLink` is cross-platform
  in .NET 6+, works fine on Linux for this purpose).
- Everything else in `src/FolderLink` (the WinForms UI, the actual
  `robocopy.exe` invocation, output parsing, admin elevation via the app
  manifest, the finalize-and-link step) — **not automated, not run**.
  robocopy isn't available on Linux and WinForms can't render here. This
  has been reviewed by reading, not by execution. Treat it as "should
  work" rather than "verified."
- The disk-space-insufficient refusal path specifically has no test
  (hard to simulate a full disk without a mock `DriveInfo`, which the
  current code doesn't use). If you add one, you'll likely need to
  introduce a seam (an injectable size/free-space provider) rather than
  calling `DriveInfo` directly.

## Two real bugs found from a user's pasted log (fixed by reading code, not by running it)

A user pasted the app's own log output from a real run, and it showed two
distinct bugs in `src/FolderLink/Program.cs` `CompleteTransfer`/`StartTransfer`.
Both were diagnosed and fixed purely by reading code and decoding the log
bytes by hand — this is a good example of the "reviewed by reading, not by
execution" caveat above actually paying off, but also a reminder that it's
not a substitute for a real Windows run if you ever get one.

1. **The earlier claim in this file that `/UNICODE` fixed robocopy's
   Chinese-filename encoding was incomplete.** `/UNICODE` only affects
   robocopy's per-file/per-dir *listing* lines. Its banner and summary
   block (`ROBOCOPY :: Robust File Copy for Windows`, the `Source :` /
   `Dest :` / `Options :` lines, the final totals table) are **always**
   written in the OEM codepage, regardless of `/UNICODE`. This app passes
   `/NFL /NDL`, so it never prints the per-file/per-dir lines that
   `/UNICODE` actually affects — meaning 100% of what robocopy ever wrote
   to this app's log was OEM-codepage text being force-decoded as UTF-16,
   producing garbage (confirmed by hand-decoding a pasted log: the
   mojibake was byte-for-byte "ROBOCOPY" etc. misread as UTF-16LE pairs).
   Fix: dropped `/UNICODE` entirely (no benefit left once `/NFL /NDL` are
   present) and switched `StandardOutputEncoding`/`StandardErrorEncoding`
   to the real OEM codepage via P/Invoke `GetOEMCP()` +
   `Encoding.GetEncoding(...)`, which needs the `System.Text.Encoding.CodePages`
   package (`CodePagesEncodingProvider`) registered in `Main()` since .NET's
   built-in encodings don't include legacy codepages like 950 (Traditional
   Chinese Big5). If a future change starts passing robocopy without
   `/NFL /NDL` (to get a real file list in the log), this whole approach
   needs revisiting — a single fixed encoding can't correctly decode a
   stream that mixes OEM banner text with UTF-16 file-list lines.
2. **A successful move could get stuck refusing to create the symlink.**
   `robocopy /MOVE` deletes source directories once they're fully emptied
   out — including the root source folder itself once everything's been
   moved out of it. The finalize step tried to `Directory.EnumerateFileSystemEntries(source, ...)`
   to confirm the source was empty before deleting it and creating the
   symlink, and treated *any* exception (including the expected
   `DirectoryNotFoundException` when robocopy had already removed the
   whole folder) as "can't confirm safety, don't link" — so a fully
   successful, fully-moved transfer could end with the files correctly at
   the destination but no symlink, and a "please check manually" message.
   Fix: check `Directory.Exists(source)` first; a missing source directory
   is now treated as the (expected) success case rather than an error.

## Git/repo constraints discovered this session

- This session's push credentials are scoped to **one branch**
  (`claude/windows-file-transfer-symlinks-yt5iiz`). Pushing a git tag
  fails with a 403 even though pushing commits to that branch works —
  don't assume tag pushes will succeed; check before relying on one.
- The repo had zero commits before this work started, so this branch is
  currently GitHub's **default branch** (confirmed via
  `git remote show origin` → `HEAD branch:`). That's why README download
  links point at the branch name directly instead of `main` or a release
  tag — there was no better stable target available. If the repo
  structure changes (a real `main` gets created, a release gets tagged
  through some other means), update those links.
- No GitHub "Release" object has ever been created here (no MCP tool was
  available to create one in the sessions so far) — `FolderLink.exe` is
  distributed by being committed directly to the repo and linked via its
  raw.githubusercontent.com URL. It's ~69 MB, which is under GitHub's
  100 MB hard limit but above the 50 MB warning threshold (you'll see a
  `GH001: Large files detected` warning on push — that's expected and
  non-blocking, not an error to fix).

## Known gaps / ideas if you're looking for follow-up work

- `legacy-script-version/FolderLink.ps1` still has the original codepage
  bug (hardcoded assumption instead of robocopy `/UNICODE` + UTF-16,
  which the C# rewrite fixed) — not fixed there since that version is
  explicitly deprecated, but worth knowing if someone reports garbled
  Chinese filenames while using the legacy script specifically.
- No code signing (would need a paid cert, out of scope so far) — the
  README's SmartScreen/Defender section explains this tradeoff to users,
  don't re-litigate it without new information.
- No custom app icon (`.exe` uses the .NET default).
- The exe is large (~69 MB) because it's self-contained; framework-
  dependent publishing would shrink it a lot but requires the .NET 8
  Desktop Runtime pre-installed on the user's machine, which most
  Windows PCs don't have out of the box. Self-contained was chosen
  deliberately for "just works, no prerequisites" — don't switch this
  without discussing the tradeoff with the user first.
