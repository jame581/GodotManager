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
    /// operation without sufficient privileges. Shared verbatim by
    /// RegistryService.SaveAsync's global-write failure and ActivateCommand's
    /// UnauthorizedAccessException/SecurityException catches so the wording
    /// given to the user does not drift between the two call sites.
    /// </summary>
    public static string ElevationHint => OperatingSystem.IsWindows()
        ? "Global-scope installs require administrator privileges. Re-run elevated."
        : "Global-scope installs require root privileges. Re-run with sudo.";
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
