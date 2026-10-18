namespace FolderLinkApp;

/// <summary>
/// "Move back": undo a move this app made. The link at the original place
/// is swapped for an empty folder, robocopy moves the files back into it
/// (same arguments as a normal move), and the then-empty folder they came
/// from is removed. Like <see cref="TransferFinalizer"/>, it refuses
/// whenever anything is in doubt, and it never deletes a file.
/// </summary>
public static class LinkRestorer
{
    /// <summary>
    /// Returns a human-readable error if moving back should be refused, or
    /// null with <paramref name="target"/> set to the folder the link points
    /// at (absolute, normalized). <paramref name="freeSpace"/> works as in
    /// <see cref="TransferSafety.TestPreFlight"/>.
    /// </summary>
    public static string? TestRestore(string link, out string target, Func<string, long?>? freeSpace = null)
    {
        target = "";
        if (string.IsNullOrWhiteSpace(link))
            return CoreStrings.LinkPathRequired;
        if (!Path.IsPathFullyQualified(link))
            return CoreStrings.PathsMustBeAbsolute;

        string path;
        try { path = TransferPaths.Normalize(link); }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return CoreStrings.PathInvalid;
        }

        string? linkTarget;
        try { linkTarget = TransferSafety.IsReparsePoint(path) ? new DirectoryInfo(path).LinkTarget : null; }
        catch { linkTarget = null; }
        if (linkTarget == null)
            return string.Format(CoreStrings.RestoreNotALink, path);

        // A relative link target is relative to the folder holding the link.
        var parent = Path.GetDirectoryName(path) ?? path;
        target = TransferPaths.Normalize(Path.GetFullPath(StripLongPrefix(linkTarget), parent));

        if (!Directory.Exists(target))
            return string.Format(CoreStrings.RestoreTargetMissing, target);
        if (TransferSafety.IsReparsePoint(target))
            return string.Format(CoreStrings.RestoreTargetIsLink, target);

        // The link's own place, spelled without following the link itself.
        var place = Path.Combine(TransferPaths.ResolveLinks(parent), Path.GetFileName(path));
        var real = TransferPaths.ResolveLinks(target);
        if (TransferPaths.AreSame(place, real) || TransferPaths.IsInside(place, real) || TransferPaths.IsInside(real, place))
            return CoreStrings.RestoreNested;

        try
        {
            var root = Path.GetPathRoot(path);
            if (!string.IsNullOrEmpty(root) && !TransferPaths.AreSame(root, Path.GetPathRoot(real) ?? ""))
            {
                var available = (freeSpace ?? TransferSafety.DriveFreeSpace)(root);
                if (available is long have)
                {
                    var need = TransferSafety.GetDirectorySizeSafe(target);
                    if (have < need)
                        return string.Format(CoreStrings.InsufficientDiskSpace, root, TransferSafety.ToGb(need), TransferSafety.ToGb(have));
                }
            }
        }
        catch
        {
            // As in TestPreFlight: robocopy is the final judge.
        }

        return null;
    }

    /// <summary>
    /// Replaces the link with an empty folder for robocopy to move the files
    /// into. Returns null on success, or the error; the link is put back if
    /// the folder can't be made, so a failure leaves things as they were.
    /// </summary>
    public static string? SwapLinkForFolder(string link, string target)
    {
        try
        {
            // Must still be the link checked by TestRestore: deleting a
            // real folder here would be wrong (and fails when it has files).
            if (!TransferSafety.IsReparsePoint(link))
                return string.Format(CoreStrings.RestoreNotALink, link);
            Directory.Delete(link); // removes the link only, never what it points at
        }
        catch (Exception ex)
        {
            return ex.Message;
        }

        try
        {
            Directory.CreateDirectory(link);
            return null;
        }
        catch (Exception ex)
        {
            try { Directory.CreateSymbolicLink(link, target); }
            catch { /* the files are untouched at the target either way */ }
            return ex.Message;
        }
    }

    /// <summary>
    /// After robocopy moved everything back: remove the folder the files
    /// came from, only if it is empty. robocopy /MOVE usually removed it
    /// already. Linked = done; LeftoverItems / CannotVerify keep it.
    /// </summary>
    public static FinalizeResult RemoveEmptyTarget(string target)
    {
        try
        {
            if (Directory.Exists(target))
            {
                if (Directory.EnumerateFileSystemEntries(target).Any())
                {
                    var listed = TransferFinalizer.ListLeftovers(target, TransferFinalizer.MaxListedLeftovers + 1);
                    return TransferFinalizer.Leftover(listed.Take(TransferFinalizer.MaxListedLeftovers).ToList(),
                        listed.Count > TransferFinalizer.MaxListedLeftovers);
                }
                Directory.Delete(target, recursive: false);
            }
            return new FinalizeResult(FinalizeStatus.Linked, Array.Empty<string>(), false, null);
        }
        catch (Exception ex)
        {
            return new FinalizeResult(FinalizeStatus.CannotVerify, Array.Empty<string>(), false, ex.Message);
        }
    }

    private static string StripLongPrefix(string path) =>
        OperatingSystem.IsWindows() ? WindowsPaths.StripPrefix(path) : path;
}
