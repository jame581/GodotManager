using System;
using System.IO;
using GodotManager.Commands;
using GodotManager.Config;
using GodotManager.Domain;
using Spectre.Console.Cli;
using Xunit;

#nullable enable

namespace GodotManager.Tests;

public class CleanCommandTests
{
    [Fact]
    public void Clean_RemovesUserPaths()
    {
        if (OperatingSystem.IsWindows())
        {
            return; // Path overrides aimed at Linux layout; behavior on Windows is trivial.
        }

        using var temp = new TempRoot();
        temp.WithEnv("GODMAN_HOME", temp.Root);
        temp.WithEnv("GODMAN_GLOBAL_ROOT", Path.Combine(temp.Root, "global"));

        var paths = new AppPaths();

        var userInstall = paths.GetInstallRoot(InstallScope.User);
        var userShim = paths.GetShimDirectory(InstallScope.User);
        var config = paths.ConfigDirectory;

        Directory.CreateDirectory(userInstall);
        Directory.CreateDirectory(userShim);
        Directory.CreateDirectory(config);
        File.WriteAllText(Path.Combine(userInstall, "dummy"), "x");
        File.WriteAllText(Path.Combine(config, "cfg"), "x");

        var cmd = new CleanCommand(paths);
        var result = cmd.Execute(null!, new CleanCommand.Settings { Yes = true });

        Assert.Equal(0, result);
        Assert.False(Directory.Exists(userInstall));
        Assert.False(Directory.Exists(userShim));
        Assert.False(Directory.Exists(config));
    }

    [Fact]
    public void Clean_RemovesGlobalPaths_WhenOverridesSet()
    {
        if (OperatingSystem.IsWindows())
        {
            return; // Global concept not supported on Windows.
        }

        using var temp = new TempRoot();
        temp.WithEnv("GODMAN_HOME", temp.Root);
        var globalRoot = Path.Combine(temp.Root, "global-root");
        temp.WithEnv("GODMAN_GLOBAL_ROOT", globalRoot);

        var paths = new AppPaths();
        var globalInstall = paths.GetInstallRoot(InstallScope.Global);
        var globalShim = paths.GetShimDirectory(InstallScope.Global);

        Directory.CreateDirectory(globalInstall);
        Directory.CreateDirectory(globalShim);
        File.WriteAllText(Path.Combine(globalInstall, "dummy"), "x");

        var cmd = new CleanCommand(paths);
        var result = cmd.Execute(null!, new CleanCommand.Settings { Yes = true });

        Assert.Equal(0, result);
        Assert.False(Directory.Exists(globalInstall));
        Assert.False(Directory.Exists(globalShim));
    }

    private sealed class TempRoot : IDisposable
    {
        public string Root { get; } = Directory.CreateTempSubdirectory("godman-clean-test").FullName;
        private readonly (string key, string? value)[] _saved;
        private bool _disposed;

        public TempRoot()
        {
            _saved = new[]
            {
                ("GODMAN_HOME", Environment.GetEnvironmentVariable("GODMAN_HOME")),
                ("GODMAN_GLOBAL_ROOT", Environment.GetEnvironmentVariable("GODMAN_GLOBAL_ROOT")),
                ("GODOT_MANAGER_HOME", Environment.GetEnvironmentVariable("GODOT_MANAGER_HOME")),
                ("GODOT_MANAGER_GLOBAL_ROOT", Environment.GetEnvironmentVariable("GODOT_MANAGER_GLOBAL_ROOT"))
            };
        }

        public void WithEnv(string key, string value)
        {
            Environment.SetEnvironmentVariable(key, value);
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            foreach (var (key, value) in _saved)
            {
                Environment.SetEnvironmentVariable(key, value);
            }

            try
            {
                Directory.Delete(Root, recursive: true);
            }
            catch
            {
                // swallow
            }
        }
    }
}
