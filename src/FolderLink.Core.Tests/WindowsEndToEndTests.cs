using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using FolderLinkApp;
using Xunit;

namespace FolderLinkApp.Tests;

/// <summary>
/// The whole move on real Windows: pre-flight, robocopy.exe with the app's
/// own arguments, the exit code read the app's way, then the link. These run
/// on the Windows CI job (.github/workflows/windows.yml) and return at once
/// everywhere else, where robocopy and drive letters don't exist.
/// </summary>
public sealed class WindowsEndToEndTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("folderlink-win-").FullName;

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

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern uint GetShortPathNameW(string longPath, StringBuilder shortPath, uint size);

    private static int Run(string file, IEnumerable<string> args)
    {
        var info = new ProcessStartInfo(file) { UseShellExecute = false, RedirectStandardOutput = true, CreateNoWindow = true };
        foreach (var a in args) info.ArgumentList.Add(a);
        using var p = Process.Start(info)!;
        p.StandardOutput.ReadToEnd();
        p.WaitForExit();
        return p.ExitCode;
    }

    [Fact]
    public void Moves_a_folder_and_leaves_a_working_link()
    {
        if (!OperatingSystem.IsWindows()) return;
        var source = Path.Combine(_root, "來源 folder");
        var destination = Path.Combine(_root, "dest", "moved");
        Directory.CreateDirectory(Path.Combine(source, "子資料夾", "empty"));
        File.WriteAllText(Path.Combine(source, "a.txt"), "hello");
        File.WriteAllText(Path.Combine(source, "子資料夾", "中文.txt"), "你好");

        Assert.Null(TransferSafety.TestPreFlight(source, destination, realPathOf: WindowsPaths.FinalPathOf));
        var src = TransferPaths.Normalize(source);
        var dst = TransferPaths.Normalize(destination);

        var exit = Run("robocopy.exe", Robocopy.BuildArguments(src, dst));
        Assert.Equal(RobocopyOutcome.Succeeded, Robocopy.Interpret(exit, cancelRequested: false));

        var result = TransferFinalizer.ReplaceSourceWithLink(src, dst);
        Assert.Equal(FinalizeStatus.Linked, result.Status);
        Assert.NotNull(new DirectoryInfo(src).LinkTarget);
        Assert.Equal("你好", File.ReadAllText(Path.Combine(src, "子資料夾", "中文.txt")));
        Assert.True(Directory.Exists(Path.Combine(dst, "子資料夾", "empty")));
    }

    [Fact]
    public void Refuses_a_drive_root_and_a_destination_inside_the_source()
    {
        if (!OperatingSystem.IsWindows()) return;
        var source = Path.Combine(_root, "src");
        Directory.CreateDirectory(source);

        Assert.Equal(CoreStrings.SourceIsDriveRoot, TransferSafety.TestPreFlight(Path.GetPathRoot(_root)!, source));
        Assert.Equal(CoreStrings.DestinationInsideSource,
            TransferSafety.TestPreFlight(source, Path.Combine(source, "inner"), realPathOf: WindowsPaths.FinalPathOf));
        Assert.Equal(CoreStrings.PathsMustBeAbsolute, TransferSafety.TestPreFlight(@"C:relative", source));
    }

    [Fact]
    public void Sees_through_an_8_3_short_name()
    {
        if (!OperatingSystem.IsWindows()) return;
        var source = Path.Combine(_root, "A Long Source Folder Name");
        Directory.CreateDirectory(source);
        var buffer = new StringBuilder(1024);
        GetShortPathNameW(source, buffer, (uint)buffer.Capacity);
        var shortName = buffer.ToString();
        // 8.3 names can be turned off per volume; then there is nothing to hide.
        if (shortName.Length == 0 || string.Equals(shortName, source, StringComparison.OrdinalIgnoreCase)) return;

        Assert.Null(TransferSafety.TestPreFlight(source, Path.Combine(shortName, "inner"), _ => long.MaxValue));
        Assert.Equal(CoreStrings.DestinationInsideSource,
            TransferSafety.TestPreFlight(source, Path.Combine(shortName, "inner"), _ => long.MaxValue, WindowsPaths.FinalPathOf));
    }

    [Fact]
    public void Sees_through_a_subst_drive()
    {
        if (!OperatingSystem.IsWindows()) return;
        var source = Path.Combine(_root, "src");
        Directory.CreateDirectory(source);
        var letter = "QRSTUVWXY".Select(c => $"{c}:").FirstOrDefault(d => !Directory.Exists(d + "\\"));
        if (letter is null) return;
        Assert.Equal(0, Run("subst.exe", new[] { letter, source }));
        try
        {
            Assert.Equal(CoreStrings.SourceEqualsDestination,
                TransferSafety.TestPreFlight(source, letter + "\\", _ => long.MaxValue, WindowsPaths.FinalPathOf));
            Assert.Equal(CoreStrings.DestinationInsideSource,
                TransferSafety.TestPreFlight(source, letter + @"\inner", _ => long.MaxValue, WindowsPaths.FinalPathOf));
        }
        finally { Run("subst.exe", new[] { letter, "/D" }); }
    }

    [Fact]
    public void Strips_the_long_path_prefixes()
    {
        Assert.Equal(@"C:\x", WindowsPaths.StripPrefix(@"\\?\C:\x"));
        Assert.Equal(@"\\server\share\x", WindowsPaths.StripPrefix(@"\\?\UNC\server\share\x"));
        Assert.Equal(@"C:\x", WindowsPaths.StripPrefix(@"C:\x"));
    }
}
