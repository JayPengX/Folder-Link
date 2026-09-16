using FolderLinkApp;
using Xunit;

namespace FolderLinkApp.Tests;

/// <summary>
/// These exercise the exact logic that decides whether FolderLink is
/// allowed to delete the source folder and replace it with a link — the
/// one part of the app where a wrong answer means lost or corrupted
/// files. Everything here runs on real temp directories, no mocking.
/// </summary>
public sealed class TransferSafetyTests : IDisposable
{
    private readonly string _root;

    public TransferSafetyTests()
    {
        _root = Directory.CreateTempSubdirectory("folderlink-test-").FullName;
    }

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

    private static void WriteFile(string dir, string name, int sizeBytes)
    {
        File.WriteAllBytes(Path.Combine(dir, name), new byte[sizeBytes]);
    }

    // ------------------------------------------------------------------
    // TestPreFlight — refusal cases. Every one of these MUST return a
    // non-null message, i.e. the caller must never proceed to delete
    // anything.
    // ------------------------------------------------------------------

    [Theory]
    [InlineData("", "somewhere")]
    [InlineData("somewhere", "")]
    [InlineData("   ", "somewhere")]
    [InlineData(null, "somewhere")]
    public void Refuses_when_either_path_is_blank(string? source, string destination)
    {
        var error = TransferSafety.TestPreFlight(source!, destination);
        Assert.NotNull(error);
    }

    [Fact]
    public void Refuses_when_source_does_not_exist()
    {
        var source = Path.Combine(_root, "does-not-exist");
        var dest = NewDir("dest");

        var error = TransferSafety.TestPreFlight(source, dest);

        Assert.NotNull(error);
        Assert.Contains(source, error);
    }

    [Fact]
    public void Refuses_when_source_is_already_a_link()
    {
        var realTarget = NewDir("real-target");
        WriteFile(realTarget, "a.txt", 10);
        var linkedSource = Path.Combine(_root, "linked-source");
        Directory.CreateSymbolicLink(linkedSource, realTarget);
        var dest = NewDir("dest");

        var error = TransferSafety.TestPreFlight(linkedSource, dest);

        Assert.NotNull(error);
    }

    [Fact]
    public void Refuses_when_source_and_destination_are_the_same()
    {
        var dir = NewDir("same");

        var error = TransferSafety.TestPreFlight(dir, dir);

        Assert.NotNull(error);
    }

    [Fact]
    public void Refuses_when_source_and_destination_are_the_same_with_different_casing()
    {
        // NTFS (the real Windows target) is case-insensitive, so "Foo" and
        // "foo" must be treated as the same folder even though this test
        // runs on a case-sensitive Linux filesystem.
        var dir = NewDir("CaseTest");

        var error = TransferSafety.TestPreFlight(dir, dir.ToUpperInvariant());

        Assert.NotNull(error);
    }

    [Fact]
    public void Refuses_when_destination_is_inside_source()
    {
        var source = NewDir("parent");
        var dest = Path.Combine(source, "child");
        Directory.CreateDirectory(dest);

        var error = TransferSafety.TestPreFlight(source, dest);

        Assert.NotNull(error);
    }

    [Fact]
    public void Refuses_when_source_is_inside_destination()
    {
        var dest = NewDir("parent2");
        var source = Path.Combine(dest, "child2");
        Directory.CreateDirectory(source);

        var error = TransferSafety.TestPreFlight(source, dest);

        Assert.NotNull(error);
    }

    [Fact]
    public void Refuses_when_destination_is_already_a_link()
    {
        var source = NewDir("src-ok");
        WriteFile(source, "a.txt", 10);
        var realElsewhere = NewDir("real-elsewhere");
        var linkedDest = Path.Combine(_root, "linked-dest");
        Directory.CreateSymbolicLink(linkedDest, realElsewhere);

        var error = TransferSafety.TestPreFlight(source, linkedDest);

        Assert.NotNull(error);
    }

    // ------------------------------------------------------------------
    // TestPreFlight — the happy path must NOT be blocked by mistake.
    // ------------------------------------------------------------------

    [Fact]
    public void Allows_a_normal_distinct_source_and_destination()
    {
        var source = NewDir("good-source");
        WriteFile(source, "a.txt", 123);
        var dest = NewDir("good-dest");

        var error = TransferSafety.TestPreFlight(source, dest);

        Assert.Null(error);
    }

    [Fact]
    public void Allows_a_destination_that_does_not_exist_yet()
    {
        var source = NewDir("good-source2");
        WriteFile(source, "a.txt", 5);
        var dest = Path.Combine(_root, "not-created-yet");

        var error = TransferSafety.TestPreFlight(source, dest);

        Assert.Null(error);
    }

    [Fact]
    public void Allows_sibling_folders_that_merely_share_a_name_prefix()
    {
        // "source" vs "source-backup" must NOT be treated as nested —
        // a naive StartsWith without the trailing separator would wrongly
        // refuse this.
        var source = NewDir("source");
        WriteFile(source, "a.txt", 5);
        var dest = NewDir("source-backup");

        var error = TransferSafety.TestPreFlight(source, dest);

        Assert.Null(error);
    }

    // ------------------------------------------------------------------
    // GetDirectorySizeSafe — must total real bytes and skip linked-in
    // subtrees, matching robocopy's /XJ behaviour used for the actual
    // move so the disk-space pre-check and the real transfer agree.
    // ------------------------------------------------------------------

    [Fact]
    public void DirectorySize_sums_nested_files()
    {
        var root = NewDir("size-root");
        WriteFile(root, "a.txt", 100);
        var sub = Directory.CreateDirectory(Path.Combine(root, "sub")).FullName;
        WriteFile(sub, "b.txt", 250);

        var size = TransferSafety.GetDirectorySizeSafe(root);

        Assert.Equal(350, size);
    }

    [Fact]
    public void DirectorySize_ignores_linked_subdirectories()
    {
        var root = NewDir("size-root2");
        WriteFile(root, "a.txt", 100);

        var elsewhere = NewDir("size-elsewhere");
        WriteFile(elsewhere, "big.bin", 999_999);
        Directory.CreateSymbolicLink(Path.Combine(root, "linked-sub"), elsewhere);

        var size = TransferSafety.GetDirectorySizeSafe(root);

        Assert.Equal(100, size);
    }

    [Fact]
    public void DirectorySize_of_empty_folder_is_zero()
    {
        var root = NewDir("empty-root");

        Assert.Equal(0, TransferSafety.GetDirectorySizeSafe(root));
    }

    // ------------------------------------------------------------------
    // IsReparsePoint
    // ------------------------------------------------------------------

    [Fact]
    public void IsReparsePoint_is_false_for_a_normal_directory()
    {
        var dir = NewDir("plain-dir");

        Assert.False(TransferSafety.IsReparsePoint(dir));
    }

    [Fact]
    public void IsReparsePoint_is_true_for_a_symlinked_directory()
    {
        var target = NewDir("link-target");
        var link = Path.Combine(_root, "the-link");
        Directory.CreateSymbolicLink(link, target);

        Assert.True(TransferSafety.IsReparsePoint(link));
    }
}
