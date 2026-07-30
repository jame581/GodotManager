using GodotManager.Domain;
using GodotManager.Services;
using Xunit;

namespace GodotManager.Tests;

/// <summary>
/// Covers the rule deciding whether a deactivation has to be handed to an elevated
/// process. Written against TouchesMachineState rather than IsRequired, which also
/// probes the host's OS and elevation state.
/// </summary>
public class ElevatedDeactivatorTests
{
    [Fact]
    public void TouchesMachineState_DeactivatingGlobal_IsTrue()
    {
        // Clearing GODOT_HOME and stripping PATH for a global entry are
        // EnvironmentVariableTarget.Machine writes, which throw SecurityException
        // ("Requested registry access is not allowed") from an unelevated process.
        // Unlike the activate and remove gaps, this one was missing from *both*
        // front-ends -- the elevation pattern originally covered only install,
        // activate and clean.
        Assert.True(ElevatedDeactivator.TouchesMachineState(InstallScope.Global));
    }

    [Fact]
    public void TouchesMachineState_DeactivatingUser_IsFalse()
    {
        Assert.False(ElevatedDeactivator.TouchesMachineState(InstallScope.User));
    }
}
