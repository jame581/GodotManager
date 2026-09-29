using GodotManager.Domain;
using GodotManager.Services;
using GodotManager.Tests.Helpers;
using System;
using System.Threading.Tasks;
using Xunit;

namespace GodotManager.Tests;

/// <summary>
/// The activation half of an install, shared by <c>install --activate</c> and the TUI install
/// dialog (issue #4). Each used to compose the elevation predicate, the <c>Activate = false</c>
/// request split and the elevated launch by hand; this pins the one decision they now share.
/// The elevation probe is injected, so the Windows-only split is exercised on every host.
/// </summary>
public class InstallActivationPlanTests : IDisposable
{
    private readonly GodmanTestFixture _fixture = new();
    public void Dispose() => _fixture.Dispose();

    private InstallerService Installer => new(_fixture.Paths, _fixture.Registry, _fixture.Environment);

    private static InstallRequest Request(InstallScope scope, bool activate = true, bool dryRun = false) => new(
        Version: "4.5.1",
        Edition: InstallEdition.Standard,
        Platform: InstallPlatform.Windows,
        Scope: scope,
        DownloadUri: null,
        ArchivePath: "archive.zip",
        InstallPath: null,
        Activate: activate,
        Force: false,
        DryRun: dryRun);

    private async Task SeedActiveAsync(InstallScope scope)
    {
        var entry = InstallEntryFactory.Create(scope: scope, version: "4.4.0");
        var registry = new InstallRegistry();
        registry.Installs.Add(entry);
        registry.MarkActive(entry.Id);
        await _fixture.Registry.SaveAsync(registry);
    }

    private static readonly Func<InstallScope, InstallScope?, bool> ElevationNeeded = (_, _) => true;
    private static readonly Func<InstallScope, InstallScope?, bool> NoElevation = (_, _) => false;

    [Fact]
    public async Task UserInstallOverActiveGlobal_NeedingElevation_SplitsTheActivationOff()
    {
        await SeedActiveAsync(InstallScope.Global);

        var plan = await Installer.PlanActivationAsync(Request(InstallScope.User), ElevationNeeded);

        Assert.True(plan.ActivateSeparately);
        Assert.False(plan.Request.Activate); // installs unactivated; the elevated child activates
        Assert.False(plan.ActivatedInProcess);
    }

    [Fact]
    public async Task UserInstallOverActiveGlobal_WithoutNeedingElevation_ActivatesInProcess()
    {
        // Linux, or an already-elevated Windows process: nothing to hand off.
        await SeedActiveAsync(InstallScope.Global);

        var plan = await Installer.PlanActivationAsync(Request(InstallScope.User), NoElevation);

        Assert.False(plan.ActivateSeparately);
        Assert.True(plan.Request.Activate);
        Assert.True(plan.ActivatedInProcess);
    }

    [Theory]
    [InlineData(InstallScope.User)]  // user over user: no machine-wide state
    [InlineData(null)]               // nothing active yet
    public async Task UserInstallOverANonGlobalOrAbsentActive_NeverSplits(InstallScope? active)
    {
        if (active is { } scope)
        {
            await SeedActiveAsync(scope);
        }

        var plan = await Installer.PlanActivationAsync(Request(InstallScope.User), ElevationNeeded);

        Assert.False(plan.ActivateSeparately);
        Assert.True(plan.ActivatedInProcess);
    }

    [Fact]
    public async Task GlobalInstall_NeverSplits_BecauseTheElevatedInstallChildActivatesIt()
    {
        await SeedActiveAsync(InstallScope.User);

        var plan = await Installer.PlanActivationAsync(Request(InstallScope.Global), ElevationNeeded);

        Assert.False(plan.ActivateSeparately);
        Assert.True(plan.Request.Activate);
    }

    [Theory]
    [InlineData(false, false)] // no --activate
    [InlineData(true, true)]   // --dry-run
    public async Task NothingToActivate_LeavesTheRequestAloneWithoutReadingTheRegistry(bool activate, bool dryRun)
    {
        await SeedActiveAsync(InstallScope.Global);
        var request = Request(InstallScope.User, activate, dryRun);
        var unreadable = new Func<InstallScope, InstallScope?, bool>((_, _) => throw new InvalidOperationException("predicate consulted"));

        var plan = await Installer.PlanActivationAsync(request, unreadable);

        Assert.False(plan.ActivateSeparately);
        Assert.Equal(request, plan.Request);
    }

    [Fact]
    public async Task CompleteActivation_WhenThePlanNeededNone_LaunchesNothingAndReportsNull()
    {
        var entry = InstallEntryFactory.Create(version: "4.5.1");
        var plan = new InstallActivationPlan(Request(InstallScope.User), ActivateSeparately: false);

        Assert.Null(await Installer.CompleteActivationAsync(plan, entry));
    }
}
