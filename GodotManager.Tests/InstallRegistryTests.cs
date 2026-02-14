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

    [Fact]
    public void GetActive_WhenNoActiveId_ReturnsNull()
    {
        var entry = new InstallEntry { Id = Guid.NewGuid(), Version = "4.5.1" };
        var registry = new InstallRegistry
        {
            Installs = { entry }
        };

        var active = registry.GetActive();

        Assert.Null(active);
    }

    [Fact]
    public void GetActive_WhenActiveIdDoesNotMatchAnyInstall_ReturnsNull()
    {
        var entry = new InstallEntry { Id = Guid.NewGuid(), Version = "4.5.1" };
        var registry = new InstallRegistry
        {
            Installs = { entry },
            ActiveId = Guid.NewGuid() // different from entry.Id
        };

        var active = registry.GetActive();

        Assert.Null(active);
    }

    [Fact]
    public void ClearActive_ResetsActiveIdAndFlags()
    {
        var a = new InstallEntry { Id = Guid.NewGuid(), Version = "4.5.1" };
        var b = new InstallEntry { Id = Guid.NewGuid(), Version = "4.5.0" };
        var registry = new InstallRegistry
        {
            Installs = { a, b }
        };

        registry.MarkActive(a.Id);
        Assert.Equal(a.Id, registry.ActiveId);
        Assert.True(a.IsActive);

        registry.ClearActive();

        Assert.Null(registry.ActiveId);
        Assert.False(a.IsActive);
        Assert.False(b.IsActive);
    }

    [Fact]
    public void MarkActive_SwitchesActiveInstall()
    {
        var a = new InstallEntry { Id = Guid.NewGuid(), Version = "4.5.1" };
        var b = new InstallEntry { Id = Guid.NewGuid(), Version = "4.5.0" };
        var registry = new InstallRegistry
        {
            Installs = { a, b }
        };

        registry.MarkActive(a.Id);
        Assert.True(a.IsActive);
        Assert.False(b.IsActive);

        registry.MarkActive(b.Id);
        Assert.False(a.IsActive);
        Assert.True(b.IsActive);
        Assert.Equal(b.Id, registry.ActiveId);
    }
}
