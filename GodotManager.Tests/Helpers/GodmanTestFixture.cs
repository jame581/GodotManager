using GodotManager.Config;
using GodotManager.Services;
using System;
using System.IO;

namespace GodotManager.Tests.Helpers;

/// <summary>
/// Shared test fixture that creates an isolated temp directory, overrides env vars,
/// and constructs AppPaths/RegistryService/EnvironmentService for testing.
/// Properly saves and restores all 4 env vars on Dispose.
/// </summary>
internal sealed class GodmanTestFixture : IDisposable
{
    public string TempRoot { get; }
    public AppPaths Paths { get; }
    public RegistryService Registry { get; }
    public EnvironmentService Environment { get; }

    private readonly (string Key, string? Value)[] _savedEnvVars;
    private bool _disposed;

    public GodmanTestFixture()
    {
        TempRoot = Path.Combine(Path.GetTempPath(), "godman-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(TempRoot);

        _savedEnvVars = new[]
        {
            ("GODMAN_HOME", System.Environment.GetEnvironmentVariable("GODMAN_HOME")),
            ("GODMAN_GLOBAL_ROOT", System.Environment.GetEnvironmentVariable("GODMAN_GLOBAL_ROOT")),
            ("GODOT_MANAGER_HOME", System.Environment.GetEnvironmentVariable("GODOT_MANAGER_HOME")),
            ("GODOT_MANAGER_GLOBAL_ROOT", System.Environment.GetEnvironmentVariable("GODOT_MANAGER_GLOBAL_ROOT"))
        };

        System.Environment.SetEnvironmentVariable("GODMAN_HOME", TempRoot);
        System.Environment.SetEnvironmentVariable("GODMAN_GLOBAL_ROOT", Path.Combine(TempRoot, "global"));

        Paths = new AppPaths();
        Registry = new RegistryService(Paths);
        Environment = new EnvironmentService(Paths, diagnostics: null);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        foreach (var (key, value) in _savedEnvVars)
        {
            System.Environment.SetEnvironmentVariable(key, value);
        }

        try
        {
            if (Directory.Exists(TempRoot))
                Directory.Delete(TempRoot, recursive: true);
        }
        catch
        {
            // Best effort cleanup
        }
    }
}
