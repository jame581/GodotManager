using GodotManager.Services;
using System.IO;
using Xunit;

namespace GodotManager.Tests;

/// <summary>
/// Covers the executable lookup EnvironmentService writes into the godot shim.
/// </summary>
/// <remarks>
/// The <c>windows</c> flag is passed explicitly rather than read from
/// OperatingSystem.IsWindows() so both layouts are exercised on either CI leg --
/// the .NET/mono nesting this pins exists on Linux too.
/// </remarks>
public class GodotExecutableLocatorTests
{
    private static string NewDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "godman-locator-" + Path.GetRandomFileName());
        Directory.CreateDirectory(dir);
        return dir;
    }

    [Fact]
    public void Find_StandardBuild_ReturnsTheExecutableAtTheRoot()
    {
        var root = NewDir();
        try
        {
            var exe = Path.Combine(root, "Godot_v4.7.1-stable_win64.exe");
            File.WriteAllText(exe, "");

            Assert.Equal(exe, GodotExecutableLocator.Find(root, windows: true));
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void Find_DotNetBuild_DescendsIntoTheNestedMonoDirectory()
    {
        // The bug this pins. A .NET/mono archive extracts into its own
        // Godot_vX-stable_mono_win64/ folder, so the binary is one level below the
        // install directory. A TopDirectoryOnly search finds nothing, leaving the
        // shim pointing at a fabricated <install>\<foldername>.exe that never
        // existed -- `godot` then fails with "is not recognized as an internal or
        // external command" for every .NET-edition install.
        var root = NewDir();
        try
        {
            var nested = Path.Combine(root, "Godot_v4.4-stable_mono_win64");
            Directory.CreateDirectory(nested);
            Directory.CreateDirectory(Path.Combine(nested, "GodotSharp"));
            var exe = Path.Combine(nested, "Godot_v4.4-stable_mono_win64.exe");
            File.WriteAllText(exe, "");

            Assert.Equal(exe, GodotExecutableLocator.Find(root, windows: true));
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void Find_PrefersTheGuiBinaryOverTheConsoleVariant()
    {
        // Godot ships both; _console opens an extra console window, so picking it
        // by directory-enumeration order would be a visible regression.
        var root = NewDir();
        try
        {
            var gui = Path.Combine(root, "Godot_v4.7.1-stable_win64.exe");
            var console = Path.Combine(root, "Godot_v4.7.1-stable_win64_console.exe");
            File.WriteAllText(console, "");
            File.WriteAllText(gui, "");

            Assert.Equal(gui, GodotExecutableLocator.Find(root, windows: true));
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void Find_LinuxDotNetBuild_DescendsIntoTheNestedDirectory()
    {
        var root = NewDir();
        try
        {
            var nested = Path.Combine(root, "Godot_v4.4-stable_mono_linux_x86_64");
            Directory.CreateDirectory(nested);
            var bin = Path.Combine(nested, "Godot_v4.4-stable_mono_linux.x86_64");
            File.WriteAllText(bin, "");

            Assert.Equal(bin, GodotExecutableLocator.Find(root, windows: false));
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void Find_ShallowMatchWinsOverNestedOne()
    {
        var root = NewDir();
        try
        {
            var top = Path.Combine(root, "Godot_v4.7.1-stable_win64.exe");
            File.WriteAllText(top, "");
            var nested = Path.Combine(root, "inner");
            Directory.CreateDirectory(nested);
            File.WriteAllText(Path.Combine(nested, "Godot_other.exe"), "");

            Assert.Equal(top, GodotExecutableLocator.Find(root, windows: true));
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void Find_WhenNothingMatches_ReturnsNull()
    {
        var root = NewDir();
        try
        {
            File.WriteAllText(Path.Combine(root, "README.txt"), "");

            Assert.Null(GodotExecutableLocator.Find(root, windows: true));
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void Find_MissingDirectory_ReturnsNullRatherThanThrowing()
    {
        Assert.Null(GodotExecutableLocator.Find(
            Path.Combine(Path.GetTempPath(), "godman-locator-does-not-exist"), windows: true));
    }
}
