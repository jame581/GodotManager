using GodotManager.Domain;
using System;
using System.Text.Json;
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

    [Fact]
    public void InstallEntry_ChecksumFields_RoundTripThroughJson()
    {
        var entry = new InstallEntry
        {
            Version = "4.5.1",
            Path = "/tmp/godot",
            Checksum = new string('a', 128),
            ChecksumAlgorithm = "sha512",
            ChecksumVerified = true
        };

        var restored = JsonSerializer.Deserialize<InstallEntry>(JsonSerializer.Serialize(entry));

        Assert.NotNull(restored);
        Assert.Equal("sha512", restored.ChecksumAlgorithm);
        Assert.True(restored.ChecksumVerified);
    }

    [Fact]
    public void InstallEntry_LegacyJsonWithoutChecksumFields_DeserializesWithDefaults()
    {
        // A registry written by godman <= 1.2.0.
        var legacyJson = """
            {"Id":"11111111-1111-1111-1111-111111111111","Version":"4.5.1","Edition":0,
             "Platform":1,"Scope":0,"Path":"/tmp/godot","Checksum":"abc123",
             "AddedAt":"2026-01-01T00:00:00+00:00"}
            """;

        var restored = JsonSerializer.Deserialize<InstallEntry>(legacyJson);

        Assert.NotNull(restored);
        Assert.Equal("abc123", restored.Checksum);
        Assert.Null(restored.ChecksumAlgorithm);   // null is read as legacy sha256
        Assert.False(restored.ChecksumVerified);
    }
}
