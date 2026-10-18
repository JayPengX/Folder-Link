namespace FolderLinkApp;

/// <summary>How a finished robocopy run should be treated.</summary>
public enum RobocopyOutcome
{
    /// <summary>Exit code 0-7: everything that was asked for was copied.</summary>
    Succeeded,
    /// <summary>Exit code 8-15: some files could not be copied (usually in use).</summary>
    SomeFilesFailed,
    /// <summary>Exit code 16+: robocopy couldn't do the job at all (bad path, access denied).</summary>
    Fatal,
    /// <summary>The user cancelled, or the process was killed (negative exit code).</summary>
    Cancelled,
}

/// <summary>The robocopy command line and how to read its exit code.</summary>
public static class Robocopy
{
    /// <summary>
    /// The arguments for moving <paramref name="source"/>'s contents into
    /// <paramref name="destination"/>. Pass paths through
    /// <see cref="TransferPaths.Normalize"/> first.
    /// </summary>
    /// <remarks>
    /// /MOVE   move files and folders (delete from source once copied)
    /// /E      include subfolders, empty ones too
    /// /IS     include files that look identical, so they are still moved (not left behind)
    /// /R:5 /W:5   retry a locked file 5 times, 5 seconds apart, then move on
    /// /XJ     don't follow junctions/links inside the source (no loops or duplicates);
    ///         they stay behind, which then blocks the link step on purpose
    /// /MT:8   copy up to 8 files in parallel
    /// /NFL /NDL /NP   no per-file/per-folder lines and no percentages. This also keeps
    ///         all of robocopy's output in the OEM codepage the app decodes it with:
    ///         don't add /UNICODE or drop /NFL /NDL without revisiting that (see CLAUDE.md).
    /// </remarks>
    public static IReadOnlyList<string> BuildArguments(string source, string destination) => new[]
    {
        source, destination,
        "/MOVE", "/E", "/IS", "/R:5", "/W:5", "/XJ", "/MT:8", "/NFL", "/NDL", "/NP",
    };

    /// <summary>
    /// Robocopy's exit code is a bit field: 1 copied, 2 extra files at the
    /// destination, 4 mismatches, 8 copy failures, 16 fatal error. Anything
    /// below 8 is success. A killed process reports a negative code (e.g.
    /// -1 from TerminateProcess), which must never be read as success.
    /// </summary>
    public static RobocopyOutcome Interpret(int exitCode, bool cancelRequested)
    {
        if (cancelRequested || exitCode < 0) return RobocopyOutcome.Cancelled;
        if (exitCode >= 16) return RobocopyOutcome.Fatal;
        if (exitCode >= 8) return RobocopyOutcome.SomeFilesFailed;
        return RobocopyOutcome.Succeeded;
    }
}
