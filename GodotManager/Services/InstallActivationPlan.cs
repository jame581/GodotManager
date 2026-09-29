using GodotManager.Domain;

namespace GodotManager.Services;

/// <summary>
/// How a fresh install gets activated, decided once by
/// <see cref="InstallerService.PlanActivationAsync(InstallRequest, CancellationToken)"/>
/// before anything is written and shared by <c>install --activate</c> and the TUI install
/// dialog: the request to install with (Activate cleared when the activation is split off)
/// and whether an elevated child has to do it afterwards.
/// </summary>
internal sealed record InstallActivationPlan(InstallRequest Request, bool ActivateSeparately)
{
    /// <summary>
    /// True when the install itself activated, in this process -- the case in which a
    /// machine-wide shim that could not be deleted may still outrank it
    /// (<see cref="ShimShadowing.GetWarning"/>). Not after the split: the elevated child
    /// could remove that shim itself.
    /// </summary>
    public bool ActivatedInProcess => Request.Activate;
}
