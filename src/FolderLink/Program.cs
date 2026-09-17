using System.Diagnostics;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Text;
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

        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        Application.Run(new MainForm());
    }

    internal static Encoding OemEncoding => Encoding.GetEncoding(GetOEMCP());
}

internal sealed class MainForm : Form
{
    private readonly TextBox _sourceBox;
    private readonly TextBox _destBox;
    private readonly Button _startButton;
    private readonly Button _cancelButton;
    private readonly Button _openLogButton;
    private readonly Label _statusLabel;
    private readonly ProgressBar _progressBar;
    private readonly TextBox _logBox;

    private readonly string _logDir;
    private readonly object _logLock = new();

    private Process? _roboProcess;
    private StreamWriter? _logWriter;
    private string? _pendingSource;
    private string? _pendingDest;

    public MainForm()
    {
        Text = "FolderLink — 搬移資料夾並保留原路徑捷徑";
        ClientSize = new Size(680, 470);
        MinimumSize = new Size(600, 380);
        StartPosition = FormStartPosition.CenterScreen;
        Font = new Font("Microsoft JhengHei UI", 9F);

        const int margin = 15;
        const int labelWidth = 110;
        const int boxWidth = 390;

        var sourceLabel = new Label
        {
            Text = "來源資料夾：",
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
            Text = "瀏覽...",
            Location = new Point(margin + labelWidth + boxWidth + 10, 17),
            Size = new Size(90, 24),
            Anchor = AnchorStyles.Top | AnchorStyles.Right,
        };
        sourceBrowse.Click += (_, _) =>
        {
            using var dlg = new FolderBrowserDialog { Description = "請選擇要搬移檔案的來源資料夾" };
            if (dlg.ShowDialog(this) == DialogResult.OK) _sourceBox.Text = dlg.SelectedPath;
        };

        var destLabel = new Label
        {
            Text = "目的資料夾：",
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
            Text = "瀏覽...",
            Location = new Point(margin + labelWidth + boxWidth + 10, 52),
            Size = new Size(90, 24),
            Anchor = AnchorStyles.Top | AnchorStyles.Right,
        };
        destBrowse.Click += (_, _) =>
        {
            using var dlg = new FolderBrowserDialog { Description = "請選擇（或建立）目的資料夾" };
            if (dlg.ShowDialog(this) == DialogResult.OK) _destBox.Text = dlg.SelectedPath;
        };

        _startButton = new Button
        {
            Text = "開始搬移",
            Location = new Point(margin, 95),
            Size = new Size(120, 34),
            Font = new Font("Microsoft JhengHei UI", 9F, FontStyle.Bold),
        };
        _startButton.Click += (_, _) => StartTransfer(_sourceBox.Text.Trim(), _destBox.Text.Trim());

        _cancelButton = new Button
        {
            Text = "取消",
            Location = new Point(margin + 130, 95),
            Size = new Size(100, 34),
            Enabled = false,
        };
        _cancelButton.Click += (_, _) => CancelTransfer();

        _openLogButton = new Button
        {
            Text = "開啟記錄資料夾",
            Location = new Point(margin + 240, 95),
            Size = new Size(150, 34),
        };
        _openLogButton.Click += (_, _) =>
            Process.Start(new ProcessStartInfo { FileName = _logDir, UseShellExecute = true });

        _statusLabel = new Label
        {
            Text = "狀態：閒置",
            Location = new Point(margin, 138),
            Size = new Size(620, 20),
            Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right,
        };

        _progressBar = new ProgressBar
        {
            Location = new Point(margin, 160),
            Size = new Size(645, 18),
            Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right,
        };

        _logBox = new TextBox
        {
            Multiline = true,
            ScrollBars = ScrollBars.Vertical,
            ReadOnly = true,
            Font = new Font("Consolas", 9F),
            Location = new Point(margin, 185),
            Size = new Size(645, 250),
            Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right,
        };

        Controls.AddRange(new Control[]
        {
            sourceLabel, _sourceBox, sourceBrowse,
            destLabel, _destBox, destBrowse,
            _startButton, _cancelButton, _openLogButton,
            _statusLabel, _progressBar, _logBox,
        });

        FormClosing += OnFormClosing;

        _logDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "FolderLink", "Logs");
        Directory.CreateDirectory(_logDir);
    }

    private void SetStatus(string text) => _statusLabel.Text = $"狀態：{text}";

    private void LogLine(string text)
    {
        lock (_logLock)
        {
            _logWriter?.WriteLine(text);
        }
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
            $"此操作將會把下列位置的所有檔案與子資料夾：\n{source}\n\n搬移到：\n{destination}\n\n並將原始資料夾替換成指向新位置的符號連結。\n\n是否要繼續？",
            "確認搬移", MessageBoxButtons.YesNo, MessageBoxIcon.Question);
        if (confirm != DialogResult.Yes) return;

        Directory.CreateDirectory(destination);

        _pendingSource = source;
        _pendingDest = destination;

        var stamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
        var logPath = Path.Combine(_logDir, $"transfer_{stamp}.log");

        _logBox.Clear();
        lock (_logLock)
        {
            _logWriter?.Dispose();
            _logWriter = new StreamWriter(logPath, append: false, Encoding.UTF8) { AutoFlush = true };
        }

        LogLine($"搬移中：\n  來源：{source}\n  目的：{destination}\n");
        SetStatus("正在複製檔案...");
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
            LogLine($"無法啟動 robocopy：{ex.Message}");
            SetStatus("啟動失敗");
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
            LogLine($"\nRobocopy 回報發生錯誤（結束代碼：{exitCode}）。");
            LogLine("部分檔案可能正被使用中（鎖定），重試多次後仍無法搬移。");
            LogLine("已成功複製的檔案，已從來源資料夾中移除。");
            LogLine("請先關閉正在使用剩餘檔案的程式，再按一次「開始搬移」— 已搬移的檔案不會重複處理。");
            SetStatus("已完成但發生錯誤 — 請查看記錄");
            SetBusy(false);
            MessageBox.Show(
                this,
                "部分檔案因正在使用中而無法搬移。\n\n請關閉使用這些檔案的程式，然後重新執行搬移 — 已搬移的檔案不會重複處理。\n\n原始資料夾將維持不變（尚未建立捷徑），因此不會遺失任何檔案。",
                "FolderLink", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        LogLine("\n所有檔案皆已成功複製，正在完成最後步驟...");
        SetStatus("正在完成最後步驟...");

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
                LogLine($"警告：無法確認來源資料夾是否已清空（{ex.Message}），為安全起見不會建立捷徑。");
                SetStatus("已完成但需要人工確認 — 請查看記錄");
                SetBusy(false);
                return;
            }

            if (hasLeftover)
            {
                LogLine("警告：來源資料夾中仍有殘留項目（將保留原資料夾，不建立捷徑）：");
                foreach (var entry in Directory.EnumerateFileSystemEntries(source, "*", SearchOption.AllDirectories))
                    LogLine($"  {entry}");
                SetStatus("已完成但仍有殘留檔案 — 請查看記錄");
                SetBusy(false);
                return;
            }

            if (Directory.Exists(source))
                Directory.Delete(source, recursive: true);
            Directory.CreateSymbolicLink(source, destination);
            LogLine($"捷徑已建立：\n  {source}  -->  {destination}");
            SetStatus("完成");
            MessageBox.Show(
                this,
                $"搬移完成。\n\n所有檔案現在都位於：\n{destination}\n\n並已在原始位置建立符號連結，讓現有的捷徑能夠繼續正常運作。",
                "FolderLink", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
        catch (Exception ex)
        {
            LogLine($"完成最後步驟時發生錯誤：{ex.Message}");
            SetStatus("收尾失敗 — 請查看記錄");
            MessageBox.Show(
                this,
                $"檔案已搬移完成，但建立捷徑失敗：\n{ex.Message}\n\n您可以手動建立捷徑，例如：\nmklink /D \"{source}\" \"{destination}\"",
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
            LogLine("\n使用者已取消操作。已複製的檔案已從來源移除；其餘未處理的檔案維持原狀，且尚未建立捷徑。");
            SetStatus("已取消");
        }
        SetBusy(false);
    }

    private void OnFormClosing(object? sender, FormClosingEventArgs e)
    {
        if (_roboProcess is { HasExited: false })
        {
            var r = MessageBox.Show(this, "搬移作業仍在進行中，確定要取消並離開嗎？", "FolderLink",
                MessageBoxButtons.YesNo, MessageBoxIcon.Warning);
            if (r != DialogResult.Yes)
            {
                e.Cancel = true;
                return;
            }
            try { _roboProcess.Kill(entireProcessTree: true); }
            catch { /* already exiting */ }
        }
        lock (_logLock) { _logWriter?.Dispose(); }
    }
}
