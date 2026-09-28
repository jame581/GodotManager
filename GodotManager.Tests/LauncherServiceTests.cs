using GodotManager.Domain;
using GodotManager.Services;
using GodotManager.Tests.Helpers;
using System;
using System.IO;
using System.Linq;
using Xunit;

namespace GodotManager.Tests;

public class LauncherServiceTests : IDisposable
{
    private readonly GodmanTestFixture _fixture = new();
    public void Dispose() => _fixture.Dispose();

    private InstallEntry MakeInstalledEntry(string version = "4.7.2", InstallScope scope = InstallScope.User, string? dirName = null)
    {
        var folder = dirName ?? $"Godot_v{version}-stable_linux.x86_64";
        var path = Path.Combine(_fixture.TempRoot, "installs", folder);
        Directory.CreateDirectory(path);
        File.WriteAllText(Path.Combine(path, folder), "fake binary");
        return InstallEntryFactory.Create(version: version, scope: scope, path: path);
    }

    [Fact]
    public void BuildDesktopFileName_IsLowercaseSanitizedAndCarriesTheIdPrefix()
    {
        var entry = InstallEntryFactory.Create(version: "4.7.2-rc 1", edition: InstallEdition.DotNet, scope: InstallScope.Global);
        entry.Id = Guid.Parse("1a2b3c4d-0000-0000-0000-000000000000");

        Assert.Equal("godman-godot-4.7.2-rc-1-dotnet-global-1a2b3c4d.desktop", LauncherService.BuildDesktopFileName(entry));
    }

    [Fact]
    public void BuildDesktopFile_WritesTheExpectedKeys()
    {
        var entry = InstallEntryFactory.Create(version: "4.7.2");

        var text = LauncherService.BuildDesktopFile(entry, "/opt/godot/Godot", "/icons/godman-godot.svg");

        Assert.StartsWith("[Desktop Entry]\n", text);
        Assert.Contains("\nType=Application\n", text);
        Assert.Contains("\nName=Godot 4.7.2 (Standard)\n", text);
        Assert.Contains("\nExec=\"/opt/godot/Godot\"\n", text);
        Assert.Contains("\nIcon=/icons/godman-godot.svg\n", text);
        Assert.Contains("\nTerminal=false\n", text);
        Assert.Contains("\nStartupNotify=true\n", text);
    // Every version would share one WM class, so GNOME would group all editors under
    // one arbitrary entry; the entry must not claim it.
    Assert.DoesNotContain("StartupWMClass", text);
    }

    [Fact]
    public void BuildDesktopFile_EscapesExecPerDesktopEntrySpec()
    {
        // Quoting rule: ", `, $ and \ inside the quoted argument get a backslash. Then the
        // general string-escape rule doubles every backslash, and a literal % is doubled
        // so it is not read as a field code. A space needs nothing beyond the quotes.
        var entry = InstallEntryFactory.Create();

        var text = LauncherService.BuildDesktopFile(entry, """/opt/my godot/a"b$c\d%e""", "/i.svg");

        // File text is: Exec="/opt/my godot/a\\"b\\$c\\\\d%%e"
        Assert.Contains("Exec=\"/opt/my godot/a\\\\\"b\\\\$c\\\\\\\\d%%e\"\n", text);
    }

    [Fact]
    public void Create_Linux_WritesDesktopFileAndIcon()
    {
        if (OperatingSystem.IsWindows()) return;
        var entry = MakeInstalledEntry();

        _fixture.Launcher.Create(entry);

        var entryPath = LauncherService.GetEntryPath(entry, _fixture.Paths);
        Assert.True(File.Exists(entryPath));
        var binary = Path.Combine(entry.Path, Path.GetFileName(entry.Path));
        Assert.Contains($"Exec=\"{binary}\"\n", File.ReadAllText(entryPath));

        var icon = _fixture.Paths.GetLauncherIconPath(InstallScope.User)!;
        Assert.StartsWith("<svg", File.ReadAllText(icon).TrimStart());
        Assert.True(_fixture.Launcher.Exists(entry));
    }

    [Fact]
    public void Create_TwoInstallsOfTheSameVersion_GetSeparateEntries()
    {
        if (OperatingSystem.IsWindows()) return;
        var a = MakeInstalledEntry(dirName: "a");
        var b = MakeInstalledEntry(dirName: "b");

        _fixture.Launcher.Create(a);
        _fixture.Launcher.Create(b);
        _fixture.Launcher.Delete(a);

        Assert.False(_fixture.Launcher.Exists(a));
        Assert.True(_fixture.Launcher.Exists(b));
    }

    [Fact]
    public void DeleteAll_Linux_RemovesOnlyGodmanEntriesAndTheIcon()
    {
        if (OperatingSystem.IsWindows()) return;
        var entry = MakeInstalledEntry();
        _fixture.Launcher.Create(entry);
        var dir = _fixture.Paths.GetLauncherDirectory(InstallScope.User);
        var foreign = Path.Combine(dir, "org.gnome.Foo.desktop");
        File.WriteAllText(foreign, "[Desktop Entry]\n");

        var removed = _fixture.Launcher.DeleteAll(InstallScope.User, [entry]);

        Assert.False(_fixture.Launcher.Exists(entry));
        Assert.False(File.Exists(_fixture.Paths.GetLauncherIconPath(InstallScope.User)));
        Assert.True(File.Exists(foreign));
        Assert.Equal(2, removed.Count);
    }

    [Fact]
    public void Delete_WhenNothingExists_DoesNotThrow()
    {
        var entry = InstallEntryFactory.Create(path: Path.Combine(_fixture.TempRoot, "missing"));

        var ex = Record.Exception(() => _fixture.Launcher.Delete(entry));

        Assert.Null(ex);
    }
}
