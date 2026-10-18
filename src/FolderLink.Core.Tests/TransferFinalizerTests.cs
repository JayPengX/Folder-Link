using FolderLinkApp;
using Xunit;

namespace FolderLinkApp.Tests;

/// <summary>
/// The finalize step is the only place the app deletes anything itself.
/// These run it on real folders in the states robocopy can leave behind.
/// </summary>
public sealed class TransferFinalizerTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("folderlink-final-").FullName;

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); }
        catch { /* best effort cleanup */ }
    }

    private string Dest()
    {
        var dest = Directory.CreateDirectory(Path.Combine(_root, "dest")).FullName;
        File.WriteAllText(Path.Combine(dest, "moved.txt"), "moved");
        return dest;
    }

    private static void AssertIsLinkTo(string link, string target)
    {
        var info = new DirectoryInfo(link);
        Assert.Equal(target, info.LinkTarget);
        Assert.True(File.Exists(Path.Combine(link, "moved.txt")));
    }

    [Fact]
    public void Links_when_robocopy_already_removed_the_source()
    {
        var source = Path.Combine(_root, "src");
        var dest = Dest();

        var result = TransferFinalizer.ReplaceSourceWithLink(source, dest);

        Assert.Equal(FinalizeStatus.Linked, result.Status);
        AssertIsLinkTo(source, dest);
    }

    [Fact]
    public void Links_when_the_source_is_left_empty()
    {
        var source = Directory.CreateDirectory(Path.Combine(_root, "src")).FullName;
        var dest = Dest();

        var result = TransferFinalizer.ReplaceSourceWithLink(source, dest);

        Assert.Equal(FinalizeStatus.Linked, result.Status);
        AssertIsLinkTo(source, dest);
    }

    [Fact]
    public void Keeps_a_source_with_a_leftover_file_untouched()
    {
        var source = Directory.CreateDirectory(Path.Combine(_root, "src")).FullName;
        var locked = Path.Combine(source, "locked.db");
        File.WriteAllText(locked, "still here");

        var result = TransferFinalizer.ReplaceSourceWithLink(source, Dest());

        Assert.Equal(FinalizeStatus.LeftoverItems, result.Status);
        Assert.Equal(new[] { locked }, result.Leftovers);
        Assert.False(result.MoreLeftovers);
        Assert.Equal("still here", File.ReadAllText(locked));
        Assert.Null(new DirectoryInfo(source).LinkTarget);
    }

    [Fact]
    public void An_empty_leftover_subfolder_still_blocks_the_link()
    {
        var source = Directory.CreateDirectory(Path.Combine(_root, "src")).FullName;
        var sub = Directory.CreateDirectory(Path.Combine(source, "empty-sub")).FullName;

        var result = TransferFinalizer.ReplaceSourceWithLink(source, Dest());

        Assert.Equal(FinalizeStatus.LeftoverItems, result.Status);
        Assert.True(Directory.Exists(sub));
    }

    [Fact]
    public void A_leftover_link_inside_the_source_blocks_and_is_not_followed()
    {
        // robocopy /XJ leaves junctions/links behind. Their targets must
        // not be listed as if they were in the source, nor touched.
        var source = Directory.CreateDirectory(Path.Combine(_root, "src")).FullName;
        var elsewhere = Directory.CreateDirectory(Path.Combine(_root, "elsewhere")).FullName;
        File.WriteAllText(Path.Combine(elsewhere, "keep.txt"), "x");
        var inner = Path.Combine(source, "junction");
        Directory.CreateSymbolicLink(inner, elsewhere);

        var result = TransferFinalizer.ReplaceSourceWithLink(source, Dest());

        Assert.Equal(FinalizeStatus.LeftoverItems, result.Status);
        Assert.Equal(new[] { inner }, result.Leftovers);
        Assert.True(File.Exists(Path.Combine(elsewhere, "keep.txt")));
    }

    [Fact]
    public void Leftover_listing_is_capped()
    {
        var source = Directory.CreateDirectory(Path.Combine(_root, "src")).FullName;
        for (var i = 0; i < TransferFinalizer.MaxListedLeftovers + 10; i++)
            File.WriteAllText(Path.Combine(source, $"f{i}.txt"), "");

        var result = TransferFinalizer.ReplaceSourceWithLink(source, Dest());

        Assert.Equal(FinalizeStatus.LeftoverItems, result.Status);
        Assert.Equal(TransferFinalizer.MaxListedLeftovers, result.Leftovers.Count);
        Assert.True(result.MoreLeftovers);
        Assert.Equal(TransferFinalizer.MaxListedLeftovers + 10, Directory.GetFiles(source).Length);
    }

    [Fact]
    public void Leftovers_in_nested_folders_are_listed()
    {
        var source = Directory.CreateDirectory(Path.Combine(_root, "src")).FullName;
        var deep = Directory.CreateDirectory(Path.Combine(source, "a", "b")).FullName;
        File.WriteAllText(Path.Combine(deep, "deep.txt"), "x");

        var result = TransferFinalizer.ReplaceSourceWithLink(source, Dest());

        Assert.Contains(Path.Combine(deep, "deep.txt"), result.Leftovers);
    }

    [Fact]
    public void A_file_sitting_at_the_source_path_is_a_leftover()
    {
        var source = Path.Combine(_root, "src");
        File.WriteAllText(source, "not a folder");

        var result = TransferFinalizer.ReplaceSourceWithLink(source, Dest());

        Assert.Equal(FinalizeStatus.LeftoverItems, result.Status);
        Assert.Equal("not a folder", File.ReadAllText(source));
    }

    [Fact]
    public void Missing_destination_stops_without_touching_the_source()
    {
        var source = Directory.CreateDirectory(Path.Combine(_root, "src")).FullName;

        var result = TransferFinalizer.ReplaceSourceWithLink(source, Path.Combine(_root, "no-dest"));

        Assert.Equal(FinalizeStatus.CannotVerify, result.Status);
        Assert.True(Directory.Exists(source));
        Assert.Null(new DirectoryInfo(source).LinkTarget);
    }

    [Fact]
    public void Link_failure_is_reported_as_LinkFailed()
    {
        // Parent of the source path is missing: nothing to delete, and the
        // link can't be created.
        var source = Path.Combine(_root, "missing-parent", "src");

        var result = TransferFinalizer.ReplaceSourceWithLink(source, Dest());

        Assert.Equal(FinalizeStatus.LinkFailed, result.Status);
        Assert.NotNull(result.Error);
        Assert.True(File.Exists(Path.Combine(_root, "dest", "moved.txt")));
    }

    [Fact]
    public void End_to_end_preflight_then_finalize_on_a_simulated_move()
    {
        // Simulate what robocopy /MOVE does, then run the real finalize.
        var source = Directory.CreateDirectory(Path.Combine(_root, "Docs")).FullName;
        File.WriteAllText(Path.Combine(source, "moved.txt"), "moved");
        var dest = Path.Combine(_root, "NewHome");

        Assert.Null(TransferSafety.TestPreFlight(source + Path.DirectorySeparatorChar, dest, _ => long.MaxValue));
        var src = TransferPaths.Normalize(source + Path.DirectorySeparatorChar);
        var dst = TransferPaths.Normalize(dest);

        Directory.CreateDirectory(dst);
        File.Move(Path.Combine(src, "moved.txt"), Path.Combine(dst, "moved.txt"));
        Directory.Delete(src);

        var result = TransferFinalizer.ReplaceSourceWithLink(src, dst);

        Assert.Equal(FinalizeStatus.Linked, result.Status);
        AssertIsLinkTo(src, dst);
        Assert.True(Path.IsPathFullyQualified(new DirectoryInfo(src).LinkTarget!));

        // Running the app again on the now-linked folder is refused.
        Assert.NotNull(TransferSafety.TestPreFlight(src, Path.Combine(_root, "Third"), _ => long.MaxValue));
    }
}
