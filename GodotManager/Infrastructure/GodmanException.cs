namespace GodotManager.Infrastructure;

/// <summary>
/// An expected, user-actionable failure. Rendered as a message plus an
/// optional hint naming the remedy, never as a stack trace.
/// </summary>
internal class GodmanException : Exception
{
    public string? Hint { get; }

    public GodmanException(string message, string? hint = null, Exception? inner = null)
        : base(message, inner)
    {
        Hint = hint;
    }

    /// <summary>
    /// The standard remedy for a failure caused by attempting a global-scope
    /// operation without sufficient privileges, for callers that do not have the
    /// command's arguments (RegistryService.SaveAsync's global-write failure). Same
    /// text as <see cref="ElevationHintFor"/> with no arguments, so the wording does
    /// not drift between call sites.
    /// </summary>
    public static string ElevationHint => ElevationHintFor(null);

    /// <summary>
    /// This failure with its placeholder <see cref="ElevationHint"/> replaced by one naming
    /// <paramref name="arguments"/>; any other hint is left as it is. For a command whose
    /// late failure (RegistryService.SaveAsync, which has no arguments) reaches its catch.
    /// </summary>
    public GodmanException WithArguments(IReadOnlyList<string>? arguments) =>
        Hint == ElevationHint && arguments is { Count: > 0 }
            ? new GodmanException(Message, ElevationHintFor(arguments), this)
            : this;

    /// <summary>
    /// The remedy naming the exact command to re-run. <paramref name="arguments"/> are
    /// the command's own arguments, program excluded (Spectre's CommandContext.Arguments,
    /// which is the process's arguments minus the program or <c>dotnet …dll</c>).
    ///
    /// On Linux it spells the elevated form out through <see cref="ElevatedCommandLine"/>:
    /// a bare "re-run with sudo" loses GODMAN_GLOBAL_ROOT (sudo drops it) and names a
    /// godman sudo may not find. Without arguments -- a caller that has none -- they
    /// appear as a placeholder.
    /// </summary>
    public static string ElevationHintFor(IReadOnlyList<string>? arguments)
    {
        if (OperatingSystem.IsWindows())
        {
            return "Global-scope installs require administrator privileges. Re-run elevated.";
        }

        return arguments is { Count: > 0 }
            ? $"Global-scope installs require root privileges. Re-run it with sudo: {ElevatedCommandLine.RenderArguments(arguments)}"
            : $"Global-scope installs require root privileges. Re-run the same command with sudo: {ElevatedCommandLine.Render("<same arguments>")}";
    }
}

/// <summary>
/// A downloaded archive did not match the checksum published upstream.
/// The archive is deleted before this is thrown.
/// </summary>
internal sealed class ChecksumMismatchException : GodmanException
{
    public string ArchiveName { get; }
    public string Expected { get; }
    public string Actual { get; }
    public Uri SumsUri { get; }

    public ChecksumMismatchException(string archiveName, string expected, string actual, Uri sumsUri)
        : base(
            $"Checksum mismatch for {archiveName}. Expected {expected}, got {actual}. The downloaded archive has been deleted.",
            $"The download did not match the checksum published at {sumsUri}. Retry the install; if it keeps failing, the upstream asset or your connection may be at fault.")
    {
        ArchiveName = archiveName;
        Expected = expected;
        Actual = actual;
        SumsUri = sumsUri;
    }
}
