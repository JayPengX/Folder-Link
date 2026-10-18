using FolderLinkApp;
using Xunit;

namespace FolderLinkApp.Tests;

/// <summary>
/// Pre-flight edge cases found in the audit: each one used to be allowed
/// through, and each one could end with the source deleted and a link
/// pointing at the wrong place (or a move that can never be finished).
/// </summary>
public sealed class PreFlightEdgeCaseTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("folderlink-edge-").FullName;

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); }
        catch { /* best effort cleanup */ }
    }

    private string NewDir(string name)
    {
        var path = Path.Combine(_root, name);
        Directory.CreateDirectory(path);
        return path;
    }

    private static long? Plenty(string _) => long.MaxValue;

    [Fact]
    public void Refuses_a_relative_destination()
    {
        // A relative destination would become a *relative* symlink target,
        // resolved against the link's own folder rather than the folder
        // robocopy actually copied into.
        var source = NewDir("src");

        Assert.Equal(CoreStrings.PathsMustBeAbsolute, TransferSafety.TestPreFlight(source, "relative-dest", Plenty));
    }

    [Fact]
    public void Refuses_a_relative_source()
    {
        Assert.Equal(CoreStrings.PathsMustBeAbsolute, TransferSafety.TestPreFlight("relative-src", NewDir("dst"), Plenty));
    }

    [Fact]
    public void Refuses_a_drive_root_as_the_source()
    {
        // A volume root can never be deleted and replaced by a link, so
        // the move would empty the drive and then fail to link it.
        var root = Path.GetPathRoot(_root)!;

        Assert.Equal(CoreStrings.SourceIsDriveRoot, TransferSafety.TestPreFlight(root, NewDir("dst"), Plenty));
    }

    [Fact]
    public void Allows_a_drive_root_as_the_destination_when_not_nested()
    {
        // The destination may be a root; on Linux "/" contains the temp
        // source, so this is the nested refusal instead. What matters is
        // that the root check itself does not reject destinations.
        var source = NewDir("src");
        var error = TransferSafety.TestPreFlight(source, Path.GetPathRoot(_root)!, Plenty);

        Assert.Equal(CoreStrings.SourceInsideDestination, error);
    }

    [Fact]
    public void Refuses_a_destination_that_is_an_existing_file()
    {
        var source = NewDir("src");
        var file = Path.Combine(_root, "a-file");
        File.WriteAllText(file, "x");

        Assert.Equal(CoreStrings.DestinationIsAFile, TransferSafety.TestPreFlight(source, file, Plenty));
    }

    [Fact]
    public void Refuses_a_destination_that_is_a_dangling_link()
    {
        var source = NewDir("src");
        var dangling = Path.Combine(_root, "dangling");
        Directory.CreateSymbolicLink(dangling, Path.Combine(_root, "gone"));

        Assert.Equal(CoreStrings.DestinationIsAlreadyLink, TransferSafety.TestPreFlight(source, dangling, Plenty));
    }

    [Fact]
    public void Refuses_a_destination_reached_through_a_link_into_the_source()
    {
        // dest = <alias>/inner where <alias> is a link to the source: the
        // strings don't look nested, the folders are.
        var source = NewDir("src");
        var alias = Path.Combine(_root, "alias");
        Directory.CreateSymbolicLink(alias, source);

        Assert.Equal(CoreStrings.DestinationInsideSource,
            TransferSafety.TestPreFlight(source, Path.Combine(alias, "inner"), Plenty));
    }

    [Fact]
    public void Refuses_a_destination_that_is_the_source_through_a_parent_link()
    {
        var parent = NewDir("parent");
        var source = Path.Combine(parent, "src");
        Directory.CreateDirectory(source);
        var parentAlias = Path.Combine(_root, "parent-alias");
        Directory.CreateSymbolicLink(parentAlias, parent);

        Assert.Equal(CoreStrings.SourceEqualsDestination,
            TransferSafety.TestPreFlight(source, Path.Combine(parentAlias, "src"), Plenty));
    }

    [Fact]
    public void Refuses_a_source_that_sits_inside_the_destination_through_a_link()
    {
        var dest = NewDir("dst");
        var source = Path.Combine(dest, "src");
        Directory.CreateDirectory(source);
        var destAlias = Path.Combine(_root, "dst-alias");
        Directory.CreateSymbolicLink(destAlias, dest);

        // Source spelled via the alias, destination spelled directly.
        Assert.Equal(CoreStrings.SourceInsideDestination,
            TransferSafety.TestPreFlight(Path.Combine(destAlias, "src"), dest, Plenty));
    }

    [Fact]
    public void Trailing_separators_do_not_change_the_answer()
    {
        var source = NewDir("src");
        var dest = NewDir("dst");
        var sep = Path.DirectorySeparatorChar;

        Assert.Null(TransferSafety.TestPreFlight(source + sep, dest + sep + sep, Plenty));
        Assert.Equal(CoreStrings.SourceEqualsDestination,
            TransferSafety.TestPreFlight(source + sep, source, Plenty));
    }

    [Fact]
    public void Dot_segments_are_resolved_before_comparing()
    {
        var source = NewDir("src");
        var sneaky = Path.Combine(_root, "dst", "..", "src", "child");

        Assert.Equal(CoreStrings.DestinationInsideSource,
            TransferSafety.TestPreFlight(source, sneaky, Plenty));
    }

    [Fact]
    public void Refuses_a_link_cycle_without_hanging()
    {
        var source = NewDir("src");
        var a = Path.Combine(_root, "cycle-a");
        var b = Path.Combine(_root, "cycle-b");
        Directory.CreateSymbolicLink(a, b);
        Directory.CreateSymbolicLink(b, a);

        // The destination is itself a (broken, cyclic) link: refused, and
        // resolving it must not loop.
        Assert.Equal(CoreStrings.DestinationIsAlreadyLink, TransferSafety.TestPreFlight(source, a, Plenty));
    }

    [Fact]
    public void Missing_source_is_reported_before_any_size_walk()
    {
        var walked = false;
        var error = TransferSafety.TestPreFlight(Path.Combine(_root, "nope"), NewDir("dst"), _ => { walked = true; return 0; });

        Assert.NotNull(error);
        Assert.False(walked);
    }

    [Fact]
    public void Every_refusal_message_resolves_in_both_languages()
    {
        // A key missing from either .resx falls back to the bare key name.
        var original = Thread.CurrentThread.CurrentUICulture;
        try
        {
            foreach (var culture in new[] { "en", "zh-TW" })
            {
                Thread.CurrentThread.CurrentUICulture = System.Globalization.CultureInfo.GetCultureInfo(culture);
                foreach (var prop in typeof(CoreStrings).GetProperties(System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic))
                {
                    var value = (string)prop.GetValue(null)!;
                    Assert.NotEqual(prop.Name, value);
                }
            }
        }
        finally
        {
            Thread.CurrentThread.CurrentUICulture = original;
        }
    }

    // ------------------------------------------------------------------
    // Disk space — now testable through the free-space seam.
    // ------------------------------------------------------------------

    [Fact]
    public void Refuses_when_destination_drive_has_less_free_space_than_the_source_needs()
    {
        var source = NewDir("src");
        File.WriteAllBytes(Path.Combine(source, "f.bin"), new byte[1000]);

        string? askedRoot = null;
        var error = TransferSafety.TestPreFlight(source, NewDir("dst"), root => { askedRoot = root; return 999; });

        Assert.Equal(Path.GetPathRoot(_root), askedRoot);
        Assert.NotNull(error);
        Assert.Contains(askedRoot!, error);
    }

    [Fact]
    public void Allows_when_free_space_exactly_matches_the_source_size()
    {
        var source = NewDir("src");
        File.WriteAllBytes(Path.Combine(source, "f.bin"), new byte[1000]);

        Assert.Null(TransferSafety.TestPreFlight(source, NewDir("dst"), _ => 1000));
    }

    [Fact]
    public void Allows_when_free_space_is_unknown()
    {
        // e.g. a network share DriveInfo can't inspect: robocopy decides.
        var source = NewDir("src");
        File.WriteAllBytes(Path.Combine(source, "f.bin"), new byte[1000]);

        Assert.Null(TransferSafety.TestPreFlight(source, NewDir("dst"), _ => null));
        Assert.Null(TransferSafety.TestPreFlight(source, NewDir("dst2"), _ => throw new IOException("boom")));
    }
}
