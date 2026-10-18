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

## Where it's tested: Linux here, real Windows in CI

Sessions work from Linux. Since October 2026, `.github/workflows/windows.yml`
runs on `windows-latest` on every push: the Core tests, including
`WindowsEndToEndTests` (a real robocopy move to a link, a drive root, an 8.3
name, a `subst` drive; these return at once on non-Windows), and a Release
build of the app. Check that workflow's run after a push. What CI still
can't do is click through the window itself. A person running the actual
`FolderLink.exe` end-to-end is still the most valuable check there is.

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

Every decision that could lose files now lives in `src/FolderLink.Core`
(plain .NET, runs on Linux) and is tested in `src/FolderLink.Core.Tests`
(`dotnet test`, 115 tests, real temp folders and real symlinks, no mocks):

- `TransferSafety.TestPreFlight(source, destination, freeSpace?)` — every
  refusal. `freeSpace` maps a volume root to free bytes (null = unknown)
  and defaults to `DriveInfo`; it is the seam that finally lets the
  disk-full refusal be tested.
- `TransferPaths` — `Normalize` (absolute, `..` resolved, no trailing
  separator except on a root), `AreSame`/`IsInside` (case-insensitive,
  separator-aware so `source` vs `source-backup` isn't "nested"),
  `IsRoot`, and `ResolveLinks` (replaces any link along a path with its
  final target, bounded against cycles).
- `Robocopy.BuildArguments` / `Robocopy.Interpret(exitCode, cancelRequested)`.
- `TransferFinalizer.ReplaceSourceWithLink` — the only code that deletes
  anything.
- `LinkRestorer` — "Move back" (undo a move): `TestRestore` (must be a
  link to an existing real folder, not nested, disk space when the
  volumes differ), `SwapLinkForFolder` (removes only the link, puts it
  back if the empty folder can't be made), `PutLinkBack` (when robocopy
  never started), `RemoveEmptyTarget` (after
  robocopy moved the files back; non-recursive, lists leftovers).
  The Windows CI test moves a folder and then moves it back.
- `ResourceFileTests` parses both projects' `.resx` files and hand-written
  `*.Designer.cs`: every key in both languages, no unused keys, and the
  same `{n}` placeholders in en and zh-TW.

What is still **not automated**: the WinForms window in
`src/FolderLink/Program.cs` (its buttons, OEM decoding of robocopy's output
shown in the log box, the elevation manifest). It's just wiring around the
Core calls above, and CI only builds it. Windows-only path shapes (8.3 names,
`subst` drives, drive roots) now run in CI's `WindowsEndToEndTests`;
`\\server\share` and junctions are still reviewed, not run. A path's real
spelling comes from `WindowsPaths.FinalPathOf` (GetFinalPathNameByHandleW),
which `TransferSafety.TestPreFlight(..., realPathOf:)` uses for the nesting
check.

## Audit, October 2026 (bugs fixed, all with tests)

1. **A cancelled or killed run was treated as success.** `Process.Kill`
   ends robocopy with exit code -1, and the old check was only
   `exitCode >= 8`, so a cancel went on to "All files copied" and the
   finalize step (only the leftover check stopped a link). Cancel also
   re-enabled Start before robocopy had exited, and the `Exited` handler
   read the `_roboProcess` field, so a quick restart could finish the
   wrong run. Now: `Robocopy.Interpret` makes any negative code or a cancel
   request `Cancelled`; the window stays busy until the process has
   exited; `CompleteTransfer` ignores any process that isn't the current one.
2. **Relative paths made a wrongly-anchored link.** A typed `dest` became
   `CreateSymbolicLink(source, "dest")`, a relative link resolved next to
   the link, not where robocopy copied. Relative paths are now refused,
   and both paths are normalised once and used for robocopy and the link.
3. **A drive root as source was accepted** (moves the whole drive's
   contents, then can't delete the root to link it). Refused.
4. **Nesting via links wasn't seen** (`dest = <link to source>\x`, or
   the source spelled through a link to its parent): robocopy would copy
   into itself. Checked again with links resolved.
5. **A dangling link or an existing file as destination was accepted.**
   Refused (`IsReparsePoint` no longer needs `Directory.Exists` first).
6. **Leftover listing could crash into the wrong message.** It re-ran a
   recursive enumeration outside its own try, so an error there showed
   "files moved, create the link with mklink" while files were still in
   the source; it was also unbounded (UI freeze on many leftovers). Now
   capped at 50 and inside the finalizer's checks.
7. **Source delete was recursive.** After the emptiness check it's now
   `Directory.Delete(source)` (non-recursive), so anything that appears
   in between makes it fail instead of deleting it. The finalizer also
   stops if the destination folder is missing, instead of linking to
   nothing.
8. **README promised a "destination not empty" warning that didn't
   exist.** Added (also says same-name files get overwritten; "No" is
   the default button). Exit code 16+ now has its own message instead of
   "some files are in use".
9. Output/exit events posting to a closing window could throw on a
   thread-pool thread; now guarded (`PostToUi`).

Refactors: the two Browse rows are one `CreatePathRow`, message boxes go
through `ShowMessage`, stop logic for Cancel and window-close is one
`StopRobocopy`, and the robocopy arguments are built in Core.

Owner decisions (October 2026): the disk check counts the full source even
for a same-drive move, and the owner chose to keep it strict; merging into a
destination overwrites same-named files after the warning (owner's choice);
a cancel that races robocopy finishing leaves the files at the
destination with no link (safe; since October 2026 the log says so and
gives the `mklink /D` line to run, `Robocopy.FinishedBeforeCancel`).

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
   Fix (now in `TransferFinalizer`): check `Directory.Exists(source)` first; a missing source directory
   is now treated as the (expected) success case rather than an error.

## The exe crashed at launch after the resx localization commit

After UI strings moved into `Strings.resx`, the user reported the exe
"can't boot". Cause: `src/FolderLink/FolderLink.csproj` had
`<RootNamespace>FolderLink</RootNamespace>`, so the SDK embedded the
resources as `FolderLink.Strings.resources`, but `Strings.Designer.cs`
asks `ResourceManager` for `FolderLinkApp.Strings`. That compiles fine,
then the first string lookup in `MainForm`'s constructor throws
`MissingManifestResourceException` and the WinExe dies with no window.
(The earlier comment claiming the SDK takes the name from the
Designer.cs file's namespace was wrong. Core only worked because
`FolderLink.Core.csproj` already sets `RootNamespace` to `FolderLinkApp`.)

Fix: set `RootNamespace` to `FolderLinkApp` in the app csproj. Two guards
were also added:
- `scripts/build.sh` now fails if `FolderLinkApp.Strings.resources` is
  missing from the compiled app dll.
- `Program.Main` wraps `Application.Run(new MainForm())` in a try/catch
  that shows the exception in a MessageBox, so a future startup crash
  shows an error instead of silently doing nothing.

## UI cleanup from direct user feedback

1. **The Browse button was getting cut off — two attempts.** `MainForm`
   lays itself out with hardcoded pixel `Location`/`Size` values rather
   than a designer.
   - First attempt: set `AutoScaleMode = AutoScaleMode.Dpi` alone, on the
     theory that the `Form` default (`AutoScaleMode.Font`) was inflating
     hardcoded coordinates against an implicit baseline font. The user
     reported the button **still cut off** after this shipped — which is
     itself informative: `AutoScaleMode.Dpi` only actually rescales
     anything if `AutoScaleDimensions` gives it a design-time baseline to
     compare the runtime DPI against; left unset (as it was), Dpi mode has
     no baseline and silently no-ops, so that first fix likely changed
     nothing. This means the original bug was probably never really an
     autoscale-inflation problem in the first place.
   - Second attempt (current): set `AutoScaleDimensions = new SizeF(96F, 96F)`
     alongside `AutoScaleMode.Dpi`, so Dpi scaling actually engages against
     a real "designed at 100%" baseline consistent with the
     `PerMonitorV2` declaration in `app.manifest`/`Program.Main`. Also
     switched all four buttons to `AutoSize = true` +
     `AutoSizeMode.GrowOnly` with a `MinimumSize` floor, so a button
     always grows to fit its own label as actually measured by the
     runtime font/GDI+ renderer, rather than trusting a fixed pixel width
     guessed at design time — this is the more bulletproof half of the
     fix, since it stops depending on getting DPI-scale math exactly
     right. Widened `ClientSize` (680→720) and the trailing rows to match,
     for extra margin.
   - None of this has been visually verified (no Windows access — see the
     top of this file). If it's *still* cut off after this, that's strong
     evidence the cause isn't autoscale/sizing at all — suspect something
     more fundamental, e.g. "Microsoft JhengHei UI" not being present on
     the user's system (triggering an unpredictable font substitution) or
     a screenshot/description mismatch — ask the user for an actual
     screenshot before guessing at a third layout tweak.
2. **Logs are no longer written to disk.** The app used to write each
   run's robocopy output to `%LOCALAPPDATA%\FolderLink\Logs\transfer_<timestamp>.log`
   and had an "開啟記錄資料夾" (Open Log Folder) button to jump there. The
   user considered this unwanted disk clutter, so both the file writing
   (`_logWriter`/`StreamWriter`/`_logDir`) and the button were removed.
   The on-screen log textbox in the main window is unaffected — it still
   shows the same live output during a run, it just isn't persisted
   anywhere once the app closes. README.md's old "## Logs" section
   (describing the log file path) was removed too.
3. **Dropped the redundant `Directory.CreateDirectory(destination)`
   call** in `StartTransfer`. It's unnecessary on two counts: the
   destination `FolderBrowserDialog` already has a native "Make New
   Folder" button, and robocopy itself creates any destination path
   that doesn't exist yet when it copies. (Note: `TransferSafety.TestPreFlight`
   never required the destination to pre-exist, so this doesn't change
   what preflight allows — it only removes a step that was doing nothing
   robocopy wasn't already going to do.)

## Git/repo constraints discovered this session

- `main` is the default branch (`git remote show origin` → `HEAD branch:`),
  and the README's download link is `main`'s raw `FolderLink.exe`. The old
  first branch, `claude/windows-file-transfer-symlinks-yt5iiz`, is deleted,
  so a link to it 404s. Point any new link at `main`.
- Pushing a git tag once failed with a 403 while branch pushes worked:
  don't rely on a tag push without checking it landed.
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
- The app icon (`src/FolderLink/assets/FolderLink.ico`, a folder with a link badge) is drawn by `scripts/make-icon.py` (Pillow); edit the script and re-run it rather than editing the .ico.
- The exe is large (~69 MB) because it's self-contained; framework-
  dependent publishing would shrink it a lot but requires the .NET 8
  Desktop Runtime pre-installed on the user's machine, which most
  Windows PCs don't have out of the box. Self-contained was chosen
  deliberately for "just works, no prerequisites" — don't switch this
  without discussing the tradeoff with the user first.
