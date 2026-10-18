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

        // A WinExe has no console, so an exception escaping Main would make
        // the app vanish with no visible error at all. Show it instead.
        try
        {
            Application.Run(new MainForm());
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.ToString(), "FolderLink", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
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

    // The robocopy run in progress, or null when idle.
    private Process? _roboProcess;
    private bool _cancelRequested;

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

        var (sourceLabel, sourceBox, sourceBrowse) = CreatePathRow(Strings.SourceFolderLabel, Strings.SourceBrowseDialogDescription, 20);
        var (destLabel, destBox, destBrowse) = CreatePathRow(Strings.DestFolderLabel, Strings.DestBrowseDialogDescription, 55);
        _sourceBox = sourceBox;
        _destBox = destBox;

        _startButton = new Button
        {
            Text = Strings.StartButton,
            Location = new Point(Gutter, 95),
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowOnly,
            MinimumSize = new Size(120, 34),
            Font = new Font(Program.UiFontFamily, 9F, FontStyle.Bold),
        };
        _startButton.Click += (_, _) => StartTransfer(_sourceBox.Text.Trim(), _destBox.Text.Trim());

        _cancelButton = new Button
        {
            Text = Strings.CancelButton,
            Location = new Point(Gutter + 130, 95),
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowOnly,
            MinimumSize = new Size(100, 34),
            Enabled = false,
        };
        _cancelButton.Click += (_, _) => StopRobocopy();

        _statusLabel = new Label
        {
            Text = string.Format(Strings.StatusFormat, Strings.StatusIdle),
            Location = new Point(Gutter, 138),
            Size = new Size(660, 20),
            Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right,
        };

        _progressBar = new ProgressBar
        {
            Location = new Point(Gutter, 160),
            Size = new Size(690, 18),
            Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right,
        };

        _logBox = new TextBox
        {
            Multiline = true,
            ScrollBars = ScrollBars.Vertical,
            ReadOnly = true,
            Font = new Font("Consolas", 9F),
            Location = new Point(Gutter, 185),
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

    private const int Gutter = 15;
    private const int LabelWidth = 110;
    private const int BoxWidth = 390;

    /// <summary>One "label, text box, Browse..." row.</summary>
    private (Label, TextBox, Button) CreatePathRow(string labelText, string dialogDescription, int top)
    {
        var label = new Label
        {
            Text = labelText,
            Location = new Point(Gutter, top),
            Size = new Size(LabelWidth, 20),
        };
        var box = new TextBox
        {
            Location = new Point(Gutter + LabelWidth, top - 2),
            Size = new Size(BoxWidth, 22),
            Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right,
        };
        var browse = new Button
        {
            Text = Strings.BrowseButton,
            Location = new Point(Gutter + LabelWidth + BoxWidth + 10, top - 3),
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
        browse.Click += (_, _) =>
        {
            using var dlg = new FolderBrowserDialog { Description = dialogDescription };
            if (dlg.ShowDialog(this) == DialogResult.OK) box.Text = dlg.SelectedPath;
        };
        return (label, box, browse);
    }

    private void SetStatus(string text) => _statusLabel.Text = string.Format(Strings.StatusFormat, text);

    private void ShowMessage(string text, MessageBoxIcon icon, string? title = null) =>
        MessageBox.Show(this, text, title ?? "FolderLink", MessageBoxButtons.OK, icon);

    /// <summary>
    /// Runs on the UI thread. Robocopy's output and exit events arrive on
    /// thread-pool threads and can race the window closing, when
    /// BeginInvoke throws; there is nothing left to update then.
    /// </summary>
    private void PostToUi(Action action)
    {
        try
        {
            if (IsHandleCreated && !IsDisposed) BeginInvoke(action);
        }
        catch (InvalidOperationException) { /* window is closing */ }
    }

    private void LogLine(string text) => PostToUi(() => _logBox.AppendText(text + Environment.NewLine));

    private void SetBusy(bool busy)
    {
        _startButton.Enabled = !busy;
        _sourceBox.Enabled = !busy;
        _destBox.Enabled = !busy;
        _cancelButton.Enabled = busy;
        _progressBar.Style = busy ? ProgressBarStyle.Marquee : ProgressBarStyle.Blocks;
        if (!busy) _progressBar.Value = 0;
    }

    private void EndRun(string status)
    {
        SetStatus(status);
        SetBusy(false);
    }

    // ------------------------------------------------------------------
    // Robocopy driven move. The decisions (is it safe, what does the exit
    // code mean, may the source be replaced) live in FolderLink.Core and
    // are tested there; this part only drives the process and the UI.
    // ------------------------------------------------------------------

    private void StartTransfer(string sourceInput, string destinationInput)
    {
        var preflightError = TransferSafety.TestPreFlight(sourceInput, destinationInput, realPathOf: WindowsPaths.FinalPathOf);
        if (preflightError != null)
        {
            ShowMessage(preflightError, MessageBoxIcon.Error);
            return;
        }

        // From here on use one spelling of each path everywhere: absolute,
        // no trailing separator (robocopy reads a trailing \ before a
        // closing quote as an escaped quote) and an absolute link target.
        var source = TransferPaths.Normalize(sourceInput);
        var destination = TransferPaths.Normalize(destinationInput);

        var body = string.Format(Strings.ConfirmMoveBody, source, destination);
        if (TransferSafety.HasContents(destination))
            body += Environment.NewLine + Environment.NewLine + Strings.ConfirmMoveDestNotEmpty;
        var confirm = MessageBox.Show(this, body, Strings.ConfirmMoveTitle, MessageBoxButtons.YesNo,
            MessageBoxIcon.Question, MessageBoxDefaultButton.Button2);
        if (confirm != DialogResult.Yes) return;

        // No need to pre-create the destination: the browse dialog can make
        // a new folder, and robocopy creates a missing destination itself.

        _logBox.Clear();
        LogLine(string.Format(Strings.LogTransferStarting, source, destination));
        SetStatus(Strings.StatusCopying);
        SetBusy(true);
        _cancelRequested = false;

        var psi = new ProcessStartInfo
        {
            FileName = "robocopy.exe",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            // Robocopy.BuildArguments passes /NFL /NDL, so robocopy never
            // prints a per-file or per-dir line: all it emits is the
            // banner/summary, which is always in the OEM codepage (even with
            // /UNICODE, which only affects the suppressed lines). Decoding it
            // as UTF-16 instead corrupts every line, Chinese paths included.
            StandardOutputEncoding = Program.OemEncoding,
            StandardErrorEncoding = Program.OemEncoding,
        };
        foreach (var arg in Robocopy.BuildArguments(source, destination))
            psi.ArgumentList.Add(arg);

        var process = new Process { StartInfo = psi, EnableRaisingEvents = true };
        process.OutputDataReceived += (_, e) => { if (e.Data != null) LogLine(e.Data); };
        process.ErrorDataReceived += (_, e) => { if (e.Data != null) LogLine(e.Data); };
        process.Exited += (_, _) =>
        {
            process.WaitForExit(); // ensures redirected streams are fully drained first
            PostToUi(() => CompleteTransfer(process, source, destination));
        };
        _roboProcess = process;

        try
        {
            process.Start();
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
        }
        catch (Exception ex)
        {
            _roboProcess = null;
            process.Dispose();
            LogLine(string.Format(Strings.LogRoboStartFailed, ex.Message));
            EndRun(Strings.StatusStartFailed);
        }
    }

    private void CompleteTransfer(Process process, string source, string destination)
    {
        // Only the run that is current may finish; anything else is stale.
        if (!ReferenceEquals(process, _roboProcess)) return;
        _roboProcess = null;
        var exitCode = process.ExitCode;
        process.Dispose();

        switch (Robocopy.Interpret(exitCode, _cancelRequested))
        {
            case RobocopyOutcome.Cancelled:
                // Never go on to delete/link after a cancel, whatever robocopy
                // managed to do before it was stopped.
                LogLine(Robocopy.FinishedBeforeCancel(exitCode)
                    ? string.Format(Strings.LogCancelledAfterDone, destination, source)
                    : Strings.LogCancelledByUser);
                EndRun(Strings.StatusCancelled);
                return;

            case RobocopyOutcome.Fatal:
                LogLine(string.Format(Strings.LogRoboError, exitCode));
                LogLine(Strings.LogRoboErrorFatal);
                EndRun(Strings.StatusCompletedWithErrors);
                ShowMessage(Strings.ErrorRoboFatalBody, MessageBoxIcon.Warning);
                return;

            case RobocopyOutcome.SomeFilesFailed:
                LogLine(string.Format(Strings.LogRoboError, exitCode));
                LogLine(Strings.LogRoboErrorLocked);
                LogLine(Strings.LogRoboErrorAlreadyMoved);
                LogLine(Strings.LogRoboErrorRetryHint);
                EndRun(Strings.StatusCompletedWithErrors);
                ShowMessage(Strings.ErrorSomeFilesInUseBody, MessageBoxIcon.Warning);
                return;
        }

        LogLine(Strings.LogAllFilesCopied);
        SetStatus(Strings.StatusFinishingUp);

        var result = TransferFinalizer.ReplaceSourceWithLink(source, destination);
        switch (result.Status)
        {
            case FinalizeStatus.Linked:
                LogLine(string.Format(Strings.LogSymlinkCreated, source, destination));
                EndRun(Strings.StatusCompleted);
                ShowMessage(string.Format(Strings.CompletionBody, destination), MessageBoxIcon.Information);
                break;

            case FinalizeStatus.LeftoverItems:
                LogLine(Strings.LogLeftoverWarning);
                foreach (var entry in result.Leftovers)
                    LogLine($"  {entry}");
                if (result.MoreLeftovers)
                    LogLine(Strings.LogLeftoverMore);
                EndRun(Strings.StatusCompletedLeftoverFiles);
                break;

            case FinalizeStatus.CannotVerify:
                LogLine(string.Format(Strings.LogCannotConfirmEmpty, result.Error));
                EndRun(Strings.StatusCompletedNeedsManualCheck);
                break;

            case FinalizeStatus.LinkFailed:
                LogLine(string.Format(Strings.LogFinalizeError, result.Error));
                EndRun(Strings.StatusFinalizeFailed);
                ShowMessage(string.Format(Strings.FinalizeErrorBody, result.Error, source, destination), MessageBoxIcon.Error);
                break;
        }
    }

    /// <summary>
    /// Asks robocopy to stop. The run ends (and the buttons come back) when
    /// the process has actually exited, in CompleteTransfer, so a new run
    /// can't start while the old one is still going.
    /// </summary>
    private void StopRobocopy()
    {
        if (_roboProcess is null) return;
        _cancelRequested = true;
        _cancelButton.Enabled = false;
        try { _roboProcess.Kill(entireProcessTree: true); }
        catch { /* already exiting */ }
    }

    private void OnFormClosing(object? sender, FormClosingEventArgs e)
    {
        if (_roboProcess is null) return;
        var r = MessageBox.Show(this, Strings.ClosingConfirmBody, "FolderLink",
            MessageBoxButtons.YesNo, MessageBoxIcon.Warning);
        if (r != DialogResult.Yes)
        {
            e.Cancel = true;
            return;
        }
        StopRobocopy();
    }
}
