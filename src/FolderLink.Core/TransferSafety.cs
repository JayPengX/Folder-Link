namespace FolderLinkApp;

/// <summary>
/// The pure, OS-agnostic safety logic that decides whether a move is safe
/// to attempt, and how big it is. Deliberately kept free of
/// System.Windows.Forms / robocopy / anything Windows-only, so it can be
/// exercised by fast automated tests on any platform.
/// </summary>
public static class TransferSafety
{
    public static bool IsReparsePoint(string path) =>
        (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0;

    public static long GetDirectorySizeSafe(string root)
    {
        long total = 0;
        var stack = new Stack<string>();
        stack.Push(root);
        while (stack.Count > 0)
        {
            var dir = stack.Pop();

            IEnumerable<string> files;
            try { files = Directory.EnumerateFiles(dir); }
            catch { continue; }

            foreach (var f in files)
            {
                try { total += new FileInfo(f).Length; }
                catch { /* inaccessible file — ignore for the estimate */ }
            }

            IEnumerable<string> subdirs;
            try { subdirs = Directory.EnumerateDirectories(dir); }
            catch { continue; }

            foreach (var d in subdirs)
            {
                try
                {
                    if (IsReparsePoint(d)) continue; // don't follow links, matches robocopy's /XJ
                }
                catch { continue; }
                stack.Push(d);
            }
        }
        return total;
    }

    /// <summary>
    /// Returns a human-readable error if the move should be refused, or
    /// null if it's safe to proceed.
    /// </summary>
    public static string? TestPreFlight(string source, string destination)
    {
        if (string.IsNullOrWhiteSpace(source) || string.IsNullOrWhiteSpace(destination))
            return "請同時選擇來源資料夾與目的資料夾。";

        if (!Directory.Exists(source))
            return $"來源資料夾不存在：\n{source}";

        if (IsReparsePoint(source))
            return "來源資料夾本身已經是捷徑（符號連結或接合點）。\n請選擇實際存放檔案的資料夾。";

        var srcFull = Path.GetFullPath(source).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var dstFull = Path.GetFullPath(destination).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;

        if (string.Equals(srcFull, dstFull, StringComparison.OrdinalIgnoreCase))
            return "來源資料夾與目的資料夾不能相同。";

        if (dstFull.StartsWith(srcFull, StringComparison.OrdinalIgnoreCase))
            return "目的資料夾不能位於來源資料夾之內（這樣會把資料夾複製到自己裡面）。";

        if (srcFull.StartsWith(dstFull, StringComparison.OrdinalIgnoreCase))
            return "來源資料夾不能位於目的資料夾之內。";

        if (Directory.Exists(destination) && IsReparsePoint(destination))
            return "目的資料夾本身就是捷徑，請選擇實際的資料夾作為目的地。";

        try
        {
            var totalBytes = GetDirectorySizeSafe(source);
            var destRoot = Path.GetPathRoot(Path.GetFullPath(destination));
            if (!string.IsNullOrEmpty(destRoot))
            {
                var drive = new DriveInfo(destRoot);
                if (drive.IsReady && drive.AvailableFreeSpace < totalBytes)
                {
                    var needGb = Math.Round(totalBytes / 1024.0 / 1024 / 1024, 2);
                    var haveGb = Math.Round(drive.AvailableFreeSpace / 1024.0 / 1024 / 1024, 2);
                    return $"{destRoot} 空間不足\n需要：{needGb} GB，可用：{haveGb} GB";
                }
            }
        }
        catch
        {
            // Non-fatal (e.g. a UNC destination DriveInfo can't inspect) —
            // let robocopy be the final judge.
        }

        return null;
    }
}
