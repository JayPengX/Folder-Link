using FolderLinkApp;
using Xunit;

namespace FolderLinkApp.Tests;

public sealed class TransferPathsTests : IDisposable
{
    private static readonly char Sep = Path.DirectorySeparatorChar;
    private readonly string _root = Directory.CreateTempSubdirectory("folderlink-paths-").FullName;

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); }
        catch { /* best effort cleanup */ }
    }

    [Fact]
    public void Normalize_strips_trailing_separators()
    {
        var p = Path.Combine(_root, "a");
        Assert.Equal(p, TransferPaths.Normalize(p + Sep));
        Assert.Equal(p, TransferPaths.Normalize(p + Sep + Sep));
    }

    [Fact]
    public void Normalize_keeps_the_root_separator()
    {
        var root = Path.GetPathRoot(_root)!;
        Assert.Equal(root, TransferPaths.Normalize(root));
        Assert.Equal(root, TransferPaths.Normalize(root + Sep));
    }

    [Fact]
    public void Normalize_resolves_dot_segments()
    {
        Assert.Equal(Path.Combine(_root, "b"),
            TransferPaths.Normalize(Path.Combine(_root, "a", "..", ".", "b")));
    }

    [Fact]
    public void IsRoot_only_for_the_root()
    {
        Assert.True(TransferPaths.IsRoot(Path.GetPathRoot(_root)!));
        Assert.False(TransferPaths.IsRoot(_root));
    }

    [Theory]
    [InlineData("x", "x", true)]
    [InlineData("x", "X", true)]
    [InlineData("x", "x/", true)]
    [InlineData("x", "y", false)]
    [InlineData("x", "x/y", false)]
    public void AreSame(string a, string b, bool expected)
    {
        Assert.Equal(expected, TransferPaths.AreSame(Path.Combine(_root, a), Path.Combine(_root, b)));
    }

    [Theory]
    [InlineData("x/y", "x", true)]
    [InlineData("X/y", "x", true)]
    [InlineData("x/y/z", "x", true)]
    [InlineData("x", "x", false)]
    [InlineData("x", "x/y", false)]
    [InlineData("xy", "x", false)]   // name prefix only, not nested
    [InlineData("x-backup/y", "x", false)]
    public void IsInside(string inner, string outer, bool expected)
    {
        Assert.Equal(expected, TransferPaths.IsInside(Path.Combine(_root, inner), Path.Combine(_root, outer)));
    }

    [Fact]
    public void IsInside_works_against_a_root()
    {
        Assert.True(TransferPaths.IsInside(_root, Path.GetPathRoot(_root)!));
    }

    [Fact]
    public void ResolveLinks_leaves_plain_and_missing_paths_alone()
    {
        var missing = Path.Combine(_root, "not", "there");
        Assert.Equal(missing, TransferPaths.ResolveLinks(missing));
        Assert.Equal(_root, TransferPaths.ResolveLinks(_root + Sep));
    }

    [Fact]
    public void ResolveLinks_follows_links_in_the_middle_and_chains()
    {
        var real = Directory.CreateDirectory(Path.Combine(_root, "real")).FullName;
        var hop1 = Path.Combine(_root, "hop1");
        var hop2 = Path.Combine(_root, "hop2");
        Directory.CreateSymbolicLink(hop1, real);
        Directory.CreateSymbolicLink(hop2, hop1);

        Assert.Equal(Path.Combine(real, "sub", "leaf"),
            TransferPaths.ResolveLinks(Path.Combine(hop2, "sub", "leaf")));
    }
}

public sealed class RobocopyTests
{
    [Theory]
    [InlineData(0, RobocopyOutcome.Succeeded)]   // nothing to copy
    [InlineData(1, RobocopyOutcome.Succeeded)]   // copied
    [InlineData(3, RobocopyOutcome.Succeeded)]   // copied + extras at destination
    [InlineData(7, RobocopyOutcome.Succeeded)]
    [InlineData(8, RobocopyOutcome.SomeFilesFailed)]
    [InlineData(9, RobocopyOutcome.SomeFilesFailed)]
    [InlineData(15, RobocopyOutcome.SomeFilesFailed)]
    [InlineData(16, RobocopyOutcome.Fatal)]
    [InlineData(24, RobocopyOutcome.Fatal)]
    [InlineData(-1, RobocopyOutcome.Cancelled)]  // Process.Kill => TerminateProcess(-1)
    [InlineData(int.MinValue, RobocopyOutcome.Cancelled)]
    public void Interpret_exit_codes(int code, RobocopyOutcome expected)
    {
        Assert.Equal(expected, Robocopy.Interpret(code, cancelRequested: false));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(-1)]
    public void A_cancel_request_always_wins(int code)
    {
        // Even if robocopy managed to finish before the kill landed, the
        // user said stop: never go on to delete and link.
        Assert.Equal(RobocopyOutcome.Cancelled, Robocopy.Interpret(code, cancelRequested: true));
    }

    [Fact]
    public void Arguments_start_with_source_and_destination_and_move()
    {
        var args = Robocopy.BuildArguments("S", "D");

        Assert.Equal("S", args[0]);
        Assert.Equal("D", args[1]);
        Assert.Contains("/MOVE", args);
        Assert.Contains("/E", args);
        Assert.Contains("/XJ", args);
    }

    [Fact]
    public void Arguments_keep_output_in_the_oem_codepage()
    {
        // The app decodes robocopy's output as the OEM codepage, which is
        // only right while per-file/per-dir lines are off and /UNICODE is
        // not used (see CLAUDE.md).
        var args = Robocopy.BuildArguments("S", "D");

        Assert.Contains("/NFL", args);
        Assert.Contains("/NDL", args);
        Assert.DoesNotContain(args, a => a.StartsWith("/UNICODE", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(args, a => a.StartsWith("/UNILOG", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Arguments_never_purge_or_mirror()
    {
        // /MIR and /PURGE delete destination files that aren't in the
        // source: never wanted for a move-into.
        var args = Robocopy.BuildArguments("S", "D");

        Assert.DoesNotContain("/MIR", args);
        Assert.DoesNotContain("/PURGE", args);
    }
}
