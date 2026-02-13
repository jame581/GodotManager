using GodotManager.Domain;
using System;
using Xunit;

namespace GodotManager.Tests;

public class InstallRegistryTests
{
    [Fact]
    public void MarkActive_SetsActiveAndFlags()
    {
        var a = new InstallEntry { Id = Guid.NewGuid(), Version = "4.5.1" };
        var b = new InstallEntry { Id = Guid.NewGuid(), Version = "4.5.0" };
        var registry = new InstallRegistry
        {
            Installs = { a, b }
        };

        registry.MarkActive(b.Id);

        Assert.Equal(b.Id, registry.ActiveId);
        Assert.False(a.IsActive);
        Assert.True(b.IsActive);
    }

    [Fact]
    public void GetActive_ReturnsActiveInstall()
    {
        var entry = new InstallEntry { Id = Guid.NewGuid(), Version = "4.5.1" };
        var registry = new InstallRegistry
        {
            Installs = { entry },
            ActiveId = entry.Id
        };

        var active = registry.GetActive();

        Assert.NotNull(active);
        Assert.Equal(entry.Id, active!.Id);
    }
}
