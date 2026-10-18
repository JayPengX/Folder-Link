namespace FolderLinkApp;

public enum FinalizeStatus
{
    /// <summary>The empty source folder was removed and the link created.</summary>
    Linked,
    /// <summary>Something is still in the source folder; nothing was touched.</summary>
    LeftoverItems,
    /// <summary>The state couldn't be checked safely; nothing was touched.</summary>
    CannotVerify,
    /// <summary>The source folder was removed but creating the link failed.</summary>
    LinkFailed,
}

/// <param name="Leftovers">Up to <see cref="TransferFinalizer.MaxListedLeftovers"/> leftover paths.</param>
/// <param name="MoreLeftovers">True when there are more leftovers than listed.</param>
/// <param name="Error">The exception message for CannotVerify / LinkFailed.</param>
public sealed record FinalizeResult(
    FinalizeStatus Status,
    IReadOnlyList<string> Leftovers,
    bool MoreLeftovers,
    string? Error);

/// <summary>
/// The last step after robocopy succeeded: replace the (now empty) source
/// folder with a link to the destination. This is the only place the app
/// deletes anything itself, so it refuses whenever anything is in doubt.
/// </summary>
public static class TransferFinalizer
{
    public const int MaxListedLeftovers = 50;

    /// <summary>
    /// Call only after <see cref="Robocopy.Interpret"/> said Succeeded, with
    /// the same normalized paths that were handed to robocopy.
    /// </summary>
    public static FinalizeResult ReplaceSourceWithLink(string source, string destination)
    {
        // robocopy /MOVE removes each source folder once it is empty,
        // including the source folder itself, so "gone" is the normal case.
        try
        {
            if (File.Exists(source) && !Directory.Exists(source))
                return Leftover(new[] { source }, more: false);

            if (Directory.Exists(source))
            {
                // Top level only: any entry at all (file, folder, link)
                // means "not empty", and the delete below is non-recursive.
                if (Directory.EnumerateFileSystemEntries(source).Any())
                {
                    var listed = ListLeftovers(source, MaxListedLeftovers + 1);
                    return Leftover(listed.Take(MaxListedLeftovers).ToList(), listed.Count > MaxListedLeftovers);
                }
            }

            // Never point the link at nothing: if the destination isn't there
            // (it always is after a real robocopy run), stop.
            if (!Directory.Exists(destination))
                return new FinalizeResult(FinalizeStatus.CannotVerify, Array.Empty<string>(), false,
                    new DirectoryNotFoundException(destination).Message);
        }
        catch (Exception ex)
        {
            return new FinalizeResult(FinalizeStatus.CannotVerify, Array.Empty<string>(), false, ex.Message);
        }

        try
        {
            // Non-recursive on purpose: if anything appeared since the check
            // above, this throws instead of deleting it.
            if (Directory.Exists(source))
                Directory.Delete(source, recursive: false);
        }
        catch (Exception ex)
        {
            return new FinalizeResult(FinalizeStatus.CannotVerify, Array.Empty<string>(), false, ex.Message);
        }

        try
        {
            Directory.CreateSymbolicLink(source, destination);
            return new FinalizeResult(FinalizeStatus.Linked, Array.Empty<string>(), false, null);
        }
        catch (Exception ex)
        {
            return new FinalizeResult(FinalizeStatus.LinkFailed, Array.Empty<string>(), false, ex.Message);
        }
    }

    internal static FinalizeResult Leftover(IReadOnlyList<string> items, bool more) =>
        new(FinalizeStatus.LeftoverItems, items, more, null);

    /// <summary>Walks the tree (not following links), stopping after <paramref name="limit"/> entries.</summary>
    internal static List<string> ListLeftovers(string root, int limit)
    {
        var found = new List<string>();
        var stack = new Stack<string>();
        stack.Push(root);
        while (stack.Count > 0 && found.Count < limit)
        {
            var dir = stack.Pop();
            IEnumerable<string> entries;
            try { entries = Directory.EnumerateFileSystemEntries(dir).ToList(); }
            catch { continue; }
            foreach (var entry in entries)
            {
                if (found.Count >= limit) break;
                found.Add(entry);
                if (Directory.Exists(entry) && !TransferSafety.IsReparsePoint(entry))
                    stack.Push(entry);
            }
        }
        return found;
    }
}
