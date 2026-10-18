using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace FolderLinkApp;

/// <summary>
/// The one Windows-only lookup the safety checks use. It lives in Core (not
/// the WinForms project) so the Windows CI run can test it; on other systems
/// nothing calls it.
/// </summary>
[SupportedOSPlatform("windows")]
public static class WindowsPaths
{
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFileW(
        string name, uint access, uint share, IntPtr security, uint creation, uint flags, IntPtr template);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetFinalPathNameByHandleW(SafeFileHandle handle, StringBuilder path, uint size, uint flags);

    /// <summary>
    /// The folder's own spelling from an open handle: 8.3 short names
    /// expanded, a mapped or subst drive as the place it really is, links
    /// followed. Null when it can't be opened, so the nesting check keeps the
    /// paths as written.
    /// </summary>
    public static string? FinalPathOf(string folder)
    {
        const uint OpenExisting = 3, BackupSemantics = 0x02000000, ShareAll = 7;
        using var handle = CreateFileW(folder, 0, ShareAll, IntPtr.Zero, OpenExisting, BackupSemantics, IntPtr.Zero);
        if (handle.IsInvalid) return null;
        var buffer = new StringBuilder(1024);
        var length = GetFinalPathNameByHandleW(handle, buffer, (uint)buffer.Capacity, 0);
        if (length == 0 || length >= buffer.Capacity) return null;
        return StripPrefix(buffer.ToString());
    }

    /// <summary>"\\?\C:\x" → "C:\x", "\\?\UNC\server\share\x" → "\\server\share\x".</summary>
    public static string StripPrefix(string path)
    {
        if (path.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase)) return @"\\" + path[8..];
        if (path.StartsWith(@"\\?\", StringComparison.Ordinal)) return path[4..];
        return path;
    }
}
