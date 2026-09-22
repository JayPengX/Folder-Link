using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows.Forms;

namespace FolderLinkApp;

internal static class Program
{
    [DllImport("kernel32.dll")]
    private static extern int GetOEMCP();

    [STAThread]
    private static void Main()
    {
        // robocopy's banner/summary text (and the Source:/Dest:/Files:/Options:
        // lines in it) is always written in the console OEM codepage, even
        // when /UNICODE is passed — /UNICODE only affects per-file/per-dir
        // listing lines. .NET's built-in encodings don't include legacy OEM
        // codepages (e.g. 950/Big5 on a Traditional Chinese system), so this
        // provider is needed for Encoding.GetEncoding(GetOEMCP()) to work.
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

        // Pick the initial displayed language from the Windows OS display
        // language, not the process's launch-time default culture.
        // CultureInfo.InstalledUICulture reflects the OS UI language
        // setting itself (Control Panel > Language > "Windows display
        // language" / the underlying LOCALE_SYSTEM_DEFAULT UI language),
        // which is what we want here — as opposed to CurrentUICulture,
        // which can be overridden per-user/per-process (e.g. by a
        // "regional format" override, or by whoever launches the process)
        // and would make this behave differently for the same OS
        // depending on how the app happens to be started.
        //
        // Traditional-Chinese-flavored cultures (zh-TW, zh-Hant, zh-HK,
        // zh-MO, ...) all report "zh" as their two-letter ISO language
        // name, so checking that alone (rather than an exact "zh-TW"
        // match) is what makes any of them resolve to the zh-TW
        // satellite via .NET's normal neutral/specific resource fallback.
        // Everything else — English systems, and any other/unsupported
        // language — falls back to the neutral resource set, which is
        // English (Strings.resx with no culture suffix).
        //
        // Behavior change: this used to be unconditionally Traditional
        // Chinese. A non-English, non-Chinese Windows install now sees
        // English instead of the old always-Chinese default.
        var installedUiCulture = CultureInfo.InstalledUICulture;
        var resolvedCulture = installedUiCulture.TwoLetterISOLanguageName == "zh"
            ? installedUiCulture
            : CultureInfo.GetCultureInfo("en");
        Thread.CurrentThread.CurrentUICulture = resolvedCulture;
        // Also set the default for any other thread .NET creates for us
        // (e.g. thread-pool callbacks), so resource lookups stay
        // consistent even off the main UI thread.
        CultureInfo.DefaultThreadCurrentUICulture = resolvedCulture;

        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        Application.Run(new MainForm());
    }

    internal static Encoding OemEncoding => Encoding.GetEncoding(GetOEMCP());

    // "Microsoft JhengHei UI" is a Traditional-Chinese-specific font; it
    // renders Latin text fine but isn't designed for it. Use it only when
    // we've actually resolved to the zh-TW UI, and fall back to "Segoe UI"
    // (the standard Windows UI font, present on every supported Windows
    // version) otherwise.
    internal static string UiFontFamily =>
        Thread.CurrentThread.CurrentUICulture.TwoLetterISOLanguageName == "zh"
            ? "Microsoft JhengHei UI"
            : "Segoe UI";
}

internal sealed class MainForm : Form
{
    private readonly TextBox _sourceBox;
    private readonly TextBox _destBox;
    private readonly Button _startButton;
    private readonly Button _cancelButton;
    private readonly Label _statusLabel;
    private readonly ProgressBar _progressBar;
    private readonly TextBox _logBox;

    private Process? _roboProcess;
    private string? _pendingSource;
    private string? _pendingDest;

    public MainForm()
    {
        Text = Strings.FormTitle;
        // This form is laid out with fixed pixel coordinates rather than a
        // designer. AutoScaleMode.Dpi only actually scales anything if it
        // has a recorded design-time baseline to scale from — without
        // AutoScaleDimensions set, .NET has no baseline and Dpi mode is a
        // silent no-op, leaving every hardcoded Location/Size exactly as
        // written regardless of the monitor's real DPI. 96,96 is the
        // standard "designed at 100%" baseline (matches the app.manifest's
        // PerMonitorV2 declaration), so this is what actually makes the
        // whole form + controls grow together on a scaled display.
        AutoScaleDimensions = new SizeF(96F, 96F);
        AutoScaleMode = AutoScaleMode.Dpi;
        ClientSize = new Size(720, 470);
        MinimumSize = new Size(640, 380);
        StartPosition = FormStartPosition.CenterScreen;
        Font = new Font(Program.UiFontFamily, 9F);

        const int margin = 15;
        const int labelWidth = 110;
        const int boxWidth = 390;

        var sourceLabel = new Label
        {
            Text = Strings.SourceFolderLabel,
            Location = new Point(margin, 20),
            Size = new Size(labelWidth, 20),
        };
        _sourceBox = new TextBox
        {
            Location = new Point(margin + labelWidth, 18),
            Size = new Size(boxWidth, 22),
            Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right,
        };
        var sourceBrowse = new Button
        {
            Text = Strings.BrowseButton,
            Location = new Point(margin + labelWidth + boxWidth + 10, 17),
            // AutoSize+GrowOnly means the button always grows to fit its
            // own label text (measured with the actual runtime font/DPI),
            // instead of relying on a fixed pixel width that could clip
            // the text under a different font substitution or display
            // scale than assumed here.
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowOnly,
            MinimumSize = new Size(90, 24),
            Anchor = AnchorStyles.Top | AnchorStyles.Right,
        };
        sourceBrowse.Click += (_, _) =>
        {
            using var dlg = new FolderBrowserDialog { Description = Strings.SourceBrowseDialogDescription };
            if (dlg.ShowDialog(this) == DialogResult.OK) _sourceBox.Text = dlg.SelectedPath;
        };

        var destLabel = new Label
        {
            Text = Strings.DestFolderLabel,
            Location = new Point(margin, 55),
            Size = new Size(labelWidth, 20),
        };
        _destBox = new TextBox
        {
            Location = new Point(margin + labelWidth, 53),
            Size = new Size(boxWidth, 22),
            Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right,
        };
        var destBrowse = new Button
        {
            Text = Strings.BrowseButton,
            Location = new Point(margin + labelWidth + boxWidth + 10, 52),
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowOnly,
            MinimumSize = new Size(90, 24),
            Anchor = AnchorStyles.Top | AnchorStyles.Right,
        };
        destBrowse.Click += (_, _) =>
        {
            using var dlg = new FolderBrowserDialog { Description = Strings.DestBrowseDialogDescription };
            if (dlg.ShowDialog(this) == DialogResult.OK) _destBox.Text = dlg.SelectedPath;
        };

        _startButton = new Button
        {
            Text = Strings.StartButton,
            Location = new Point(margin, 95),
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowOnly,
            MinimumSize = new Size(120, 34),
            Font = new Font(Program.UiFontFamily, 9F, FontStyle.Bold),
        };
        _startButton.Click += (_, _) => StartTransfer(_sourceBox.Text.Trim(), _destBox.Text.Trim());

        _cancelButton = new Button
        {
            Text = Strings.CancelButton,
            Location = new Point(margin + 130, 95),
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowOnly,
            MinimumSize = new Size(100, 34),
            Enabled = false,
        };
        _cancelButton.Click += (_, _) => CancelTransfer();

        _statusLabel = new Label
        {
            Text = string.Format(Strings.StatusFormat, Strings.StatusIdle),
            Location = new Point(margin, 138),
            Size = new Size(660, 20),
            Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right,
        };

        _progressBar = new ProgressBar
        {
            Location = new Point(margin, 160),
            Size = new Size(690, 18),
            Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right,
        };

        _logBox = new TextBox
        {
            Multiline = true,
            ScrollBars = ScrollBars.Vertical,
            ReadOnly = true,
            Font = new Font("Consolas", 9F),
            Location = new Point(margin, 185),
            Size = new Size(690, 250),
            Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right,
        };

        Controls.AddRange(new Control[]
        {
            sourceLabel, _sourceBox, sourceBrowse,
            destLabel, _destBox, destBrowse,
            _startButton, _cancelButton,
            _statusLabel, _progressBar, _logBox,
        });

        FormClosing += OnFormClosing;
    }

    private void SetStatus(string text) => _statusLabel.Text = string.Format(Strings.StatusFormat, text);

    private void LogLine(string text)
    {
        if (IsHandleCreated)
        {
            BeginInvoke(() => _logBox.AppendText(text + Environment.NewLine));
        }
    }

    private void SetBusy(bool busy)
    {
        _startButton.Enabled = !busy;
        _sourceBox.Enabled = !busy;
        _destBox.Enabled = !busy;
        _cancelButton.Enabled = busy;
        _progressBar.Style = busy ? ProgressBarStyle.Marquee : ProgressBarStyle.Blocks;
        if (!busy) _progressBar.Value = 0;
    }

    // ------------------------------------------------------------------
    // Robocopy driven move
    // ------------------------------------------------------------------

    private void StartTransfer(string source, string destination)
    {
        var preflightError = TransferSafety.TestPreFlight(source, destination);
        if (preflightError != null)
        {
            MessageBox.Show(this, preflightError, "FolderLink", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return;
        }

        var confirm = MessageBox.Show(
            this,
            string.Format(Strings.ConfirmMoveBody, source, destination),
            Strings.ConfirmMoveTitle, MessageBoxButtons.YesNo, MessageBoxIcon.Question);
        if (confirm != DialogResult.Yes) return;

        // No need to pre-create the destination here: the folder-browse
        // dialog above already lets the user create a new folder natively,
        // and robocopy itself creates any missing destination path when it
        // copies.

        _pendingSource = source;
        _pendingDest = destination;

        _logBox.Clear();
        LogLine(string.Format(Strings.LogTransferStarting, source, destination));
        SetStatus(Strings.StatusCopying);
        SetBusy(true);

        var psi = new ProcessStartInfo
        {
            FileName = "robocopy.exe",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            // We pass /NFL /NDL below, so robocopy never prints a per-file or
            // per-dir listing line — the only content it ever emits is the
            // banner/summary, which robocopy always writes in the OEM
            // codepage (this is true even with /UNICODE — that switch only
            // affects the per-file/per-dir lines we've suppressed). Decoding
            // it as UTF-16 instead corrupts every line, including the
            // Chinese source/dest paths in the "Source :"/"Dest :" lines.
            StandardOutputEncoding = Program.OemEncoding,
            StandardErrorEncoding = Program.OemEncoding,
        };
        // /MOVE   move files & dirs, delete from source once copied
        // /E      include subfolders, including empty ones
        // /IS     include files that already look identical, so they are still moved (not silently left behind)
        // /R:5 /W:5   retry a locked/in-use file 5 times, 5 seconds apart, then move on
        // /XJ     do not follow junctions/symlinks found inside the source (avoids loops/duplication)
        // /MT:8   copy up to 8 files in parallel
        // /NFL /NDL /NP   quieter, more readable log output (also: keeps robocopy's whole
        //                 output in the OEM codepage — see StandardOutputEncoding above)
        foreach (var arg in new[]
                 {
                     source, destination,
                     "/MOVE", "/E", "/IS", "/R:5", "/W:5", "/XJ", "/MT:8", "/NFL", "/NDL", "/NP",
                 })
        {
            psi.ArgumentList.Add(arg);
        }

        _roboProcess = new Process { StartInfo = psi, EnableRaisingEvents = true };
        _roboProcess.OutputDataReceived += (_, e) => { if (e.Data != null) LogLine(e.Data); };
        _roboProcess.ErrorDataReceived += (_, e) => { if (e.Data != null) LogLine(e.Data); };
        _roboProcess.Exited += (_, _) =>
        {
            _roboProcess.WaitForExit(); // ensures redirected streams are fully drained first
            if (IsHandleCreated) BeginInvoke(CompleteTransfer);
        };

        try
        {
            _roboProcess.Start();
            _roboProcess.BeginOutputReadLine();
            _roboProcess.BeginErrorReadLine();
        }
        catch (Exception ex)
        {
            LogLine(string.Format(Strings.LogRoboStartFailed, ex.Message));
            SetStatus(Strings.StatusStartFailed);
            SetBusy(false);
        }
    }

    private void CompleteTransfer()
    {
        if (_roboProcess is null || _pendingSource is null || _pendingDest is null) return;

        var exitCode = _roboProcess.ExitCode;
        var source = _pendingSource;
        var destination = _pendingDest;

        if (exitCode >= 8)
        {
            LogLine(string.Format(Strings.LogRoboError, exitCode));
            LogLine(Strings.LogRoboErrorLocked);
            LogLine(Strings.LogRoboErrorAlreadyMoved);
            LogLine(Strings.LogRoboErrorRetryHint);
            SetStatus(Strings.StatusCompletedWithErrors);
            SetBusy(false);
            MessageBox.Show(
                this,
                Strings.ErrorSomeFilesInUseBody,
                "FolderLink", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        LogLine(Strings.LogAllFilesCopied);
        SetStatus(Strings.StatusFinishingUp);

        try
        {
            // robocopy /MOVE deletes source directories once they're fully
            // emptied out, including the root source folder itself — so it
            // being gone entirely is the expected success case, not a
            // failure to verify. Only a real enumeration error (e.g. a
            // permissions problem on a source folder that still exists)
            // should block the symlink for safety.
            bool hasLeftover;
            try
            {
                hasLeftover = Directory.Exists(source)
                    && Directory.EnumerateFileSystemEntries(source, "*", SearchOption.AllDirectories).Any();
            }
            catch (Exception ex)
            {
                LogLine(string.Format(Strings.LogCannotConfirmEmpty, ex.Message));
                SetStatus(Strings.StatusCompletedNeedsManualCheck);
                SetBusy(false);
                return;
            }

            if (hasLeftover)
            {
                LogLine(Strings.LogLeftoverWarning);
                foreach (var entry in Directory.EnumerateFileSystemEntries(source, "*", SearchOption.AllDirectories))
                    LogLine($"  {entry}");
                SetStatus(Strings.StatusCompletedLeftoverFiles);
                SetBusy(false);
                return;
            }

            if (Directory.Exists(source))
                Directory.Delete(source, recursive: true);
            Directory.CreateSymbolicLink(source, destination);
            LogLine(string.Format(Strings.LogSymlinkCreated, source, destination));
            SetStatus(Strings.StatusCompleted);
            MessageBox.Show(
                this,
                string.Format(Strings.CompletionBody, destination),
                "FolderLink", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
        catch (Exception ex)
        {
            LogLine(string.Format(Strings.LogFinalizeError, ex.Message));
            SetStatus(Strings.StatusFinalizeFailed);
            MessageBox.Show(
                this,
                string.Format(Strings.FinalizeErrorBody, ex.Message, source, destination),
                "FolderLink", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }

        SetBusy(false);
    }

    private void CancelTransfer()
    {
        if (_roboProcess is { HasExited: false })
        {
            try { _roboProcess.Kill(entireProcessTree: true); }
            catch { /* already exiting */ }
            LogLine(Strings.LogCancelledByUser);
            SetStatus(Strings.StatusCancelled);
        }
        SetBusy(false);
    }

    private void OnFormClosing(object? sender, FormClosingEventArgs e)
    {
        if (_roboProcess is { HasExited: false })
        {
            var r = MessageBox.Show(this, Strings.ClosingConfirmBody, "FolderLink",
                MessageBoxButtons.YesNo, MessageBoxIcon.Warning);
            if (r != DialogResult.Yes)
            {
                e.Cancel = true;
                return;
            }
            try { _roboProcess.Kill(entireProcessTree: true); }
            catch { /* already exiting */ }
        }
    }
}
