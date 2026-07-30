using GodotManager.Domain;
using GodotManager.Services;
using Xunit;

namespace GodotManager.Tests;

/// <summary>
/// Covers the rule deciding whether an activation has to be handed to an elevated
/// process. Written against ElevatedActivator.TouchesMachineState rather than
/// IsRequired, which additionally probes the host's OS and elevation state and so
/// cannot assert a fixed answer on either CI leg.
/// </summary>
public class ElevatedActivatorTests
{
    [Fact]
    public void TouchesMachineState_ActivatingGlobal_IsTrue()
    {
        Assert.True(ElevatedActivator.TouchesMachineState(InstallScope.Global, InstallScope.User));
    }

    [Fact]
    public void TouchesMachineState_SwitchingAwayFromGlobal_IsTrue()
    {
        // The clause the TUI was missing. Deactivating a global entry clears
        // GODOT_HOME with an EnvironmentVariableTarget.Machine write, which throws
        // SecurityException unelevated -- before MarkActive can move the active
        // pointer off the global install. Skipping elevation here does not degrade
        // to "activates without a prompt", it strands the user on the global install
        // with every later activation throwing the same way.
        Assert.True(ElevatedActivator.TouchesMachineState(InstallScope.User, InstallScope.Global));
    }

    [Fact]
    public void TouchesMachineState_GlobalToGlobal_IsTrue()
    {
        Assert.True(ElevatedActivator.TouchesMachineState(InstallScope.Global, InstallScope.Global));
    }

    [Fact]
    public void TouchesMachineState_UserToUser_IsFalse()
    {
        // The common case must not drag a UAC prompt in front of the user.
        Assert.False(ElevatedActivator.TouchesMachineState(InstallScope.User, InstallScope.User));
    }

    [Fact]
    public void TouchesMachineState_UserWithNothingActive_IsFalse()
    {
        Assert.False(ElevatedActivator.TouchesMachineState(InstallScope.User, null));
    }

    [Fact]
    public void TouchesMachineState_GlobalWithNothingActive_IsTrue()
    {
        Assert.True(ElevatedActivator.TouchesMachineState(InstallScope.Global, null));
    }
}
