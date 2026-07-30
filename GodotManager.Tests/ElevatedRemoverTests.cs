using GodotManager.Domain;
using GodotManager.Services;
using Xunit;

namespace GodotManager.Tests;

/// <summary>
/// Covers the rule deciding whether a removal has to be handed to an elevated
/// process. Written against TouchesMachineState rather than IsRequired for the same
/// reason as ElevatedActivatorTests: IsRequired also probes the host's OS and
/// elevation state.
/// </summary>
public class ElevatedRemoverTests
{
    [Fact]
    public void TouchesMachineState_RemovingGlobal_IsTrue()
    {
        // A global entry lives in the machine-wide registry under %ProgramFiles%,
        // and its files sit there too -- neither is writable unelevated.
        Assert.True(ElevatedRemover.TouchesMachineState(InstallScope.Global));
    }

    [Fact]
    public void TouchesMachineState_RemovingUser_IsFalse()
    {
        // The ordinary case must not put a UAC prompt in front of the user; a
        // user-scope removal only ever touches their own profile.
        Assert.False(ElevatedRemover.TouchesMachineState(InstallScope.User));
    }
}
