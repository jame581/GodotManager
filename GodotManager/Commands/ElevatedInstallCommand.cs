using GodotManager.Infrastructure;
using GodotManager.Services;
using Spectre.Console;
using Spectre.Console.Cli;
using System.Text;
using System.Text.Json;

namespace GodotManager.Commands;

internal sealed class ElevatedInstallCommand : AsyncCommand<ElevatedInstallCommand.Settings>
{
    private readonly InstallerService _installer;

    public ElevatedInstallCommand(InstallerService installer)
    {
        _installer = installer;
    }

    protected override async Task<int> ExecuteAsync(CommandContext context, Settings settings, CancellationToken cancellationToken)
    {
        if (!OperatingSystem.IsWindows())
        {
            return Fail("Elevated installs are only supported on Windows.");
        }

        if (!WindowsElevationHelper.IsElevated())
        {
            return Fail("This command must be run as administrator.");
        }

        ElevatedInstallPayload? payload;
        try
        {
            var json = Encoding.UTF8.GetString(Convert.FromBase64String(settings.Payload));
            payload = JsonSerializer.Deserialize<ElevatedInstallPayload>(json);
        }
        catch (Exception ex)
        {
            return Fail($"Invalid payload: {ex.Message}");
        }

        if (payload is null)
        {
            return Fail("Invalid payload.");
        }

        var request = BuildRequest(payload);

        try
        {
            var result = await _installer.InstallAsync(request);
            AnsiConsole.MarkupLineInterpolated($"[green]Installed[/] {result.Version} ({result.Edition}, {result.Platform}) to [cyan]{result.Path}[/]");
            return 0;
        }
        catch (GodmanException ex)
        {
            return GodmanExceptionRenderer.Render("Install failed:", ex);
        }
        catch (Exception ex)
        {
            return Fail(ex.Message);
        }
    }

    /// <summary>
    /// Rebuilds the request the unelevated parent resolved. The checksum fields carry
    /// the parent's verification result, which only it could obtain — it is the process
    /// that downloaded the archive and compared it against the published sums. Without
    /// them this install re-hashes the archive and records it as unverified, which is a
    /// false statement about a download that was verified. InstallerService re-checks
    /// the hash against the archive before honouring the claimed status.
    /// </summary>
    internal static InstallRequest BuildRequest(ElevatedInstallPayload payload) =>
        new(
            payload.Version,
            payload.Edition,
            payload.Platform,
            payload.Scope,
            DownloadUri: null,
            payload.ArchivePath,
            payload.InstallPath,
            payload.Activate,
            payload.Force,
            DryRun: false,
            Known: payload.Checksum is null
                ? null
                : new KnownChecksum(payload.Checksum, payload.ChecksumAlgorithm ?? "sha512", payload.ChecksumVerified));

    private static int Fail(string message)
    {
        return GodmanExceptionRenderer.Render("Install failed:", message);
    }

    internal sealed class Settings : CommandSettings
    {
        [CommandOption("--payload <DATA>")]
        public string Payload { get; set; } = string.Empty;

        public override ValidationResult Validate()
        {
            return string.IsNullOrWhiteSpace(Payload)
                ? ValidationResult.Error("--payload is required.")
                : ValidationResult.Success();
        }
    }
}
