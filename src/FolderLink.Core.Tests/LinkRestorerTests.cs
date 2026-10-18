using FolderLinkApp;
using Xunit;

namespace FolderLinkApp.Tests;

/// <summary>
/// "Move back" on real folders and links: what it refuses, the link swap,
/// and the clean-up after robocopy (done here by moving the files by hand).
/// </summary>
public sealed class LinkRestorerTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("folderlink-restore-").FullName;

    public void Dispose()
    {
        try
        {
            foreach (var dir in Directory.EnumerateDirectories(_root, "*", SearchOption.AllDirectories).Reverse())
                if (new DirectoryInfo(dir).LinkTarget != null) Directory.Delete(dir);
            Directory.Delete(_root, recursive: true);
        }
        catch { /* best effort cleanup */ }
    }

    private (string link, string target) Moved()
    {
        var target = Directory.CreateDirectory(Path.Combine(_root, "dest", "moved")).FullName;
        File.WriteAllText(Path.Combine(target, "a.txt"), "hello");
        var link = Path.Combine(_root, "src");
        Directory.CreateSymbolicLink(link, target);
        return (link, target);
    }

    [Fact]
    public void Accepts_a_link_made_by_a_move_and_names_its_target()
    {
        var (link, target) = Moved();
        Assert.Null(LinkRestorer.TestRestore(link, out var found, _ => long.MaxValue));
        Assert.Equal(target, found);
    }

    [Fact]
    public void Resolves_a_relative_link_target_next_to_the_link()
    {
        var target = Directory.CreateDirectory(Path.Combine(_root, "moved")).FullName;
        var link = Path.Combine(_root, "src");
        Directory.CreateSymbolicLink(link, "moved");
        Assert.Null(LinkRestorer.TestRestore(link, out var found, _ => long.MaxValue));
        Assert.Equal(target, found);
    }

    [Fact]
    public void Refuses_what_is_not_a_link()
    {
        var plain = Directory.CreateDirectory(Path.Combine(_root, "plain")).FullName;
        Assert.Equal(string.Format(CoreStrings.RestoreNotALink, plain), LinkRestorer.TestRestore(plain, out _));
        var missing = Path.Combine(_root, "missing");
        Assert.Equal(string.Format(CoreStrings.RestoreNotALink, missing), LinkRestorer.TestRestore(missing, out _));
        Assert.Equal(CoreStrings.LinkPathRequired, LinkRestorer.TestRestore("  ", out _));
        Assert.Equal(CoreStrings.PathsMustBeAbsolute, LinkRestorer.TestRestore("src", out _));
    }

    [Fact]
    public void Refuses_a_dangling_link_and_a_link_to_a_link()
    {
        var (link, target) = Moved();
        var dangling = Path.Combine(_root, "dangling");
        Directory.CreateSymbolicLink(dangling, Path.Combine(_root, "gone"));
        Assert.Equal(string.Format(CoreStrings.RestoreTargetMissing, Path.Combine(_root, "gone")),
            LinkRestorer.TestRestore(dangling, out _));

        var chained = Path.Combine(_root, "chained");
        Directory.CreateSymbolicLink(chained, link);
        Assert.Equal(string.Format(CoreStrings.RestoreTargetIsLink, link), LinkRestorer.TestRestore(chained, out _));
    }

    [Fact]
    public void Refuses_a_link_inside_what_it_points_at()
    {
        var outer = Directory.CreateDirectory(Path.Combine(_root, "outer")).FullName;
        var link = Path.Combine(outer, "loop");
        Directory.CreateSymbolicLink(link, outer);
        Assert.Equal(CoreStrings.RestoreNested, LinkRestorer.TestRestore(link, out _, _ => long.MaxValue));
    }

    [Fact]
    public void Swap_leaves_an_empty_folder_and_the_files_untouched()
    {
        var (link, target) = Moved();
        Assert.Null(LinkRestorer.SwapLinkForFolder(link, target));
        Assert.True(Directory.Exists(link));
        Assert.False(TransferSafety.IsReparsePoint(link));
        Assert.Empty(Directory.EnumerateFileSystemEntries(link));
        Assert.Equal("hello", File.ReadAllText(Path.Combine(target, "a.txt")));
    }

    [Fact]
    public void Swap_refuses_a_real_folder()
    {
        var plain = Directory.CreateDirectory(Path.Combine(_root, "plain")).FullName;
        File.WriteAllText(Path.Combine(plain, "keep.txt"), "keep");
        Assert.Equal(string.Format(CoreStrings.RestoreNotALink, plain), LinkRestorer.SwapLinkForFolder(plain, _root));
        Assert.True(File.Exists(Path.Combine(plain, "keep.txt")));
    }

    [Fact]
    public void Removes_the_emptied_target_and_keeps_one_with_leftovers()
    {
        var (link, target) = Moved();
        LinkRestorer.SwapLinkForFolder(link, target);

        File.Move(Path.Combine(target, "a.txt"), Path.Combine(link, "a.txt"));
        File.WriteAllText(Path.Combine(target, "locked.txt"), "in use");
        var kept = LinkRestorer.RemoveEmptyTarget(target);
        Assert.Equal(FinalizeStatus.LeftoverItems, kept.Status);
        Assert.Contains(Path.Combine(target, "locked.txt"), kept.Leftovers);

        File.Move(Path.Combine(target, "locked.txt"), Path.Combine(link, "locked.txt"));
        Assert.Equal(FinalizeStatus.Linked, LinkRestorer.RemoveEmptyTarget(target).Status);
        Assert.False(Directory.Exists(target));
        Assert.Equal("hello", File.ReadAllText(Path.Combine(link, "a.txt")));
    }

    [Fact]
    public void A_target_robocopy_already_removed_is_done()
    {
        Assert.Equal(FinalizeStatus.Linked, LinkRestorer.RemoveEmptyTarget(Path.Combine(_root, "gone")).Status);
    }
}
