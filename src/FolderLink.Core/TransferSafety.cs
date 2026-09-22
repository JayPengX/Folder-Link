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
            return CoreStrings.BothPathsRequired;

        if (!Directory.Exists(source))
            return string.Format(CoreStrings.SourceDoesNotExist, source);

        if (IsReparsePoint(source))
            return CoreStrings.SourceIsAlreadyLink;

        var srcFull = Path.GetFullPath(source).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var dstFull = Path.GetFullPath(destination).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;

        if (string.Equals(srcFull, dstFull, StringComparison.OrdinalIgnoreCase))
            return CoreStrings.SourceEqualsDestination;

        if (dstFull.StartsWith(srcFull, StringComparison.OrdinalIgnoreCase))
            return CoreStrings.DestinationInsideSource;

        if (srcFull.StartsWith(dstFull, StringComparison.OrdinalIgnoreCase))
            return CoreStrings.SourceInsideDestination;

        if (Directory.Exists(destination) && IsReparsePoint(destination))
            return CoreStrings.DestinationIsAlreadyLink;

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
                    return string.Format(CoreStrings.InsufficientDiskSpace, destRoot, needGb, haveGb);
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
