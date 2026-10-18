namespace FolderLinkApp;

/// <summary>
/// The pure, OS-agnostic safety logic that decides whether a move is safe
/// to attempt, and how big it is. Deliberately kept free of
/// System.Windows.Forms / robocopy / anything Windows-only, so it can be
/// exercised by fast automated tests on any platform.
/// </summary>
public static class TransferSafety
{
    /// <summary>
    /// True when the path itself is a symbolic link or junction (dangling
    /// ones included). False when nothing exists at the path.
    /// </summary>
    public static bool IsReparsePoint(string path)
    {
        try { return (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0; }
        catch (FileNotFoundException) { return false; }
        catch (DirectoryNotFoundException) { return false; }
    }

    /// <summary>Free bytes on the volume whose root is given, or null when unknown.</summary>
    public static long? DriveFreeSpace(string root)
    {
        var drive = new DriveInfo(root);
        return drive.IsReady ? drive.AvailableFreeSpace : null;
    }

    /// <summary>True when the folder exists and has anything in it.</summary>
    public static bool HasContents(string path)
    {
        try { return Directory.Exists(path) && Directory.EnumerateFileSystemEntries(path).Any(); }
        catch { return false; }
    }

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
    /// null if it's safe to proceed. <paramref name="freeSpace"/> maps a
    /// volume root to its free bytes (null = unknown); it defaults to
    /// <see cref="DriveFreeSpace"/> and exists so tests can fake a full disk.
    /// <paramref name="realPathOf"/>, when given, maps an existing folder to
    /// the file system's own spelling of it (see
    /// <see cref="TransferPaths.Canonicalize"/>) for one more nesting check.
    /// </summary>
    public static string? TestPreFlight(string source, string destination, Func<string, long?>? freeSpace = null, Func<string, string?>? realPathOf = null)
    {
        if (string.IsNullOrWhiteSpace(source) || string.IsNullOrWhiteSpace(destination))
            return CoreStrings.BothPathsRequired;

        // Relative paths ("data", "C:data", "\data") would be resolved
        // against whatever the current folder happens to be, and would turn
        // into a relative link target. Only accept fully spelled-out paths.
        if (!Path.IsPathFullyQualified(source) || !Path.IsPathFullyQualified(destination))
            return CoreStrings.PathsMustBeAbsolute;

        string src, dst;
        try
        {
            src = TransferPaths.Normalize(source);
            dst = TransferPaths.Normalize(destination);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return CoreStrings.PathInvalid;
        }

        // Checked before anything walks the source: a root can't be deleted
        // and replaced by a link, so its contents would be moved for nothing.
        if (TransferPaths.IsRoot(src))
            return CoreStrings.SourceIsDriveRoot;

        if (!Directory.Exists(src))
            return string.Format(CoreStrings.SourceDoesNotExist, source);

        if (IsReparsePoint(src))
            return CoreStrings.SourceIsAlreadyLink;

        // Compare both as written and with links along the way resolved, so
        // a destination spelled through a link can't hide that it is really
        // the source or inside it.
        var nesting = CheckNesting(src, dst) ?? CheckNesting(TransferPaths.ResolveLinks(src), TransferPaths.ResolveLinks(dst));
        // And as the file system spells them, so an 8.3 short name or a
        // mapped drive can't hide it either (Windows passes the lookup).
        if (nesting == null && realPathOf != null)
            nesting = CheckNesting(TransferPaths.Canonicalize(src, realPathOf), TransferPaths.Canonicalize(dst, realPathOf));
        if (nesting != null)
            return nesting;

        if (IsReparsePoint(dst))
            return CoreStrings.DestinationIsAlreadyLink;

        if (File.Exists(dst))
            return CoreStrings.DestinationIsAFile;

        try
        {
            var destRoot = Path.GetPathRoot(dst);
            if (!string.IsNullOrEmpty(destRoot))
            {
                var available = (freeSpace ?? DriveFreeSpace)(destRoot);
                if (available is long have)
                {
                    var need = GetDirectorySizeSafe(src);
                    if (have < need)
                        return string.Format(CoreStrings.InsufficientDiskSpace, destRoot, ToGb(need), ToGb(have));
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

    private static string? CheckNesting(string src, string dst)
    {
        if (TransferPaths.AreSame(src, dst)) return CoreStrings.SourceEqualsDestination;
        if (TransferPaths.IsInside(dst, src)) return CoreStrings.DestinationInsideSource;
        if (TransferPaths.IsInside(src, dst)) return CoreStrings.SourceInsideDestination;
        return null;
    }

    internal static double ToGb(long bytes) => Math.Round(bytes / 1024.0 / 1024 / 1024, 2);
}
