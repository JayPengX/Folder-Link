namespace FolderLinkApp;

/// <summary>
/// Path handling shared by the pre-flight checks, the robocopy call and the
/// finalize step, so all three agree on exactly which folder is meant.
/// Comparisons are case-insensitive because the real target (NTFS) is.
/// </summary>
public static class TransferPaths
{
    private static readonly char[] Separators =
        { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar };

    /// <summary>
    /// Absolute path with "." / ".." resolved and no trailing separator,
    /// except for a root ("C:\", "\\server\share\", "/"), which keeps it.
    /// This is the form handed to robocopy and used as the link target:
    /// it avoids robocopy's trailing-backslash-before-quote pitfall and a
    /// relative (wrongly anchored) symlink target.
    /// </summary>
    public static string Normalize(string path)
    {
        var full = Path.GetFullPath(path);
        var root = Path.GetPathRoot(full) ?? "";
        var trimmed = full.TrimEnd(Separators);
        return trimmed.Length <= root.TrimEnd(Separators).Length ? root : trimmed;
    }

    /// <summary>True when the path is a volume or share root.</summary>
    public static bool IsRoot(string path)
    {
        var normalized = Normalize(path);
        return string.Equals(normalized, Path.GetPathRoot(normalized), StringComparison.OrdinalIgnoreCase);
    }

    public static bool AreSame(string a, string b) =>
        string.Equals(WithSeparator(Normalize(a)), WithSeparator(Normalize(b)), StringComparison.OrdinalIgnoreCase);

    /// <summary>True when <paramref name="inner"/> is strictly below <paramref name="outer"/>.</summary>
    public static bool IsInside(string inner, string outer)
    {
        var i = WithSeparator(Normalize(inner));
        var o = WithSeparator(Normalize(outer));
        return i.Length > o.Length && i.StartsWith(o, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// The path with every existing link along it (the folder itself or any
    /// parent) replaced by the folder it finally points to. Parts that don't
    /// exist yet are kept as written. Used so a destination spelled through
    /// a link can't hide that it is really inside the source.
    /// </summary>
    public static string ResolveLinks(string path)
    {
        var current = Normalize(path);
        // Bounded so a pathological chain can't loop forever.
        for (var guard = 0; guard < 64; guard++)
        {
            var root = Path.GetPathRoot(current) ?? "";
            var parts = current[root.Length..].Split(Separators, StringSplitOptions.RemoveEmptyEntries);
            var prefix = root;
            string? replaced = null;
            for (var i = 0; i < parts.Length; i++)
            {
                prefix = Path.Combine(prefix, parts[i]);
                string? target;
                try
                {
                    var info = new DirectoryInfo(prefix);
                    if (info.LinkTarget is null) continue;
                    target = info.ResolveLinkTarget(returnFinalTarget: true)?.FullName;
                }
                catch
                {
                    // Can't read or resolve (permissions, link cycle): stop
                    // resolving and compare what we have.
                    return current;
                }
                if (target is null) return current;
                replaced = Path.Combine(new[] { target }.Concat(parts.Skip(i + 1)).ToArray());
                break;
            }
            if (replaced is null) return current;
            current = Normalize(replaced);
        }
        return current;
    }

    /// <summary>
    /// The path as the file system itself spells it: the deepest part that
    /// exists is handed to <paramref name="realPathOf"/> (on Windows the
    /// final path of an open handle, which expands 8.3 short names such as
    /// PROGRA~1 and turns a mapped drive into its \\server\share path),
    /// and the parts that don't exist yet are appended as written. Returns
    /// the normalized path unchanged when nothing exists or the lookup gives
    /// no answer.
    /// </summary>
    public static string Canonicalize(string path, Func<string, string?> realPathOf)
    {
        var current = Normalize(path);
        var missing = new Stack<string>();
        while (!Directory.Exists(current))
        {
            var parent = Path.GetDirectoryName(current);
            if (parent is null) return Normalize(path);
            missing.Push(Path.GetFileName(current));
            current = parent;
        }
        string? real;
        try { real = realPathOf(current); }
        catch { return Normalize(path); }
        if (string.IsNullOrEmpty(real)) return Normalize(path);
        return Normalize(Path.Combine(new[] { real }.Concat(missing).ToArray()));
    }

    private static string WithSeparator(string normalized) =>
        normalized.EndsWith(Path.DirectorySeparatorChar) || normalized.EndsWith(Path.AltDirectorySeparatorChar)
            ? normalized
            : normalized + Path.DirectorySeparatorChar;
}
