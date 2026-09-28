using GodotManager.Config;
using GodotManager.Services;
using System;
using System.IO;

namespace GodotManager.Tests.Helpers;

/// <summary>
/// Shared test fixture that creates an isolated temp directory, overrides env vars,
/// and constructs AppPaths/RegistryService/EnvironmentService for testing.
/// Properly saves and restores all 5 env vars on Dispose.
/// </summary>
internal sealed class GodmanTestFixture : IDisposable
{
    public string TempRoot { get; }
    public AppPaths Paths { get; }
    public RegistryService Registry { get; }
    public EnvironmentService Environment { get; }
    public LauncherService Launcher { get; }

    private readonly (string Key, string? Value)[] _savedEnvVars;
    private readonly (string Key, string? Value)[] _savedPersistentVars;
    private bool _disposed;

    /// <param name="globalRoot">
    /// GODMAN_GLOBAL_ROOT relative to <see cref="TempRoot"/>; defaults to <c>global</c>.
    /// With <paramref name="globalRootVerbatim"/> it is set exactly as given instead (for
    /// tests of a relative value, run with the working directory inside a temp directory).
    /// </param>
    /// <param name="seed">
    /// Runs with the resolved GODMAN_GLOBAL_ROOT value after the environment is set and
    /// before <see cref="AppPaths"/> is constructed, for tests about what the constructor
    /// does with a layout that is already on disk.
    /// </param>
    public GodmanTestFixture(string globalRoot = "global", Action<string>? seed = null, bool globalRootVerbatim = false)
    {
        TempRoot = Path.Combine(Path.GetTempPath(), "godman-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(TempRoot);

        _savedEnvVars = new[]
        {
            ("GODMAN_HOME", System.Environment.GetEnvironmentVariable("GODMAN_HOME")),
            ("GODMAN_GLOBAL_ROOT", System.Environment.GetEnvironmentVariable("GODMAN_GLOBAL_ROOT")),
            ("GODOT_MANAGER_HOME", System.Environment.GetEnvironmentVariable("GODOT_MANAGER_HOME")),
            ("GODOT_MANAGER_GLOBAL_ROOT", System.Environment.GetEnvironmentVariable("GODOT_MANAGER_GLOBAL_ROOT")),
            ("GODMAN_LAUNCHER_ROOT", System.Environment.GetEnvironmentVariable("GODMAN_LAUNCHER_ROOT"))
        };

        // AppPaths only redirects on-disk locations. EnvironmentService.ApplyWindows
        // additionally writes GODOT_HOME and appends its shim directory to the
        // persisted *User* PATH, and those writes go to the real registry no matter
        // what GODMAN_HOME points at -- so without this snapshot every activating
        // test permanently appends its own temp shim directory to the developer's
        // PATH. That accumulated to 49 dead entries (4.4 KB, 78% of the value) on
        // one machine before it was noticed.
        _savedPersistentVars = ReadPersistentVars();

        System.Environment.SetEnvironmentVariable("GODMAN_HOME", TempRoot);
        var globalRootPath = globalRootVerbatim ? globalRoot : Path.Combine(TempRoot, globalRoot);
        System.Environment.SetEnvironmentVariable("GODMAN_GLOBAL_ROOT", globalRootPath);
        // Launcher entries resolve to the real desktop locations regardless of GODMAN_HOME,
        // so without this every install/activate test would write into the developer's real
        // app menu (Linux) or Start Menu (Windows).
        System.Environment.SetEnvironmentVariable("GODMAN_LAUNCHER_ROOT", Path.Combine(TempRoot, "launcher"));

        seed?.Invoke(globalRootPath);

        Paths = new AppPaths();
        Registry = new RegistryService(Paths);
        Launcher = new LauncherService(Paths);
        Environment = new EnvironmentService(Paths, diagnostics: null, Launcher);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        foreach (var (key, value) in _savedEnvVars)
        {
            System.Environment.SetEnvironmentVariable(key, value);
        }

        RestorePersistentVars();

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

    /// <summary>
    /// The User-scope variables EnvironmentService persists on Windows. Empty on
    /// other platforms, where the User target is not a separate store and nothing
    /// outlives the process.
    /// </summary>
    private static (string Key, string? Value)[] ReadPersistentVars()
    {
        if (!OperatingSystem.IsWindows())
        {
            return [];
        }

        return
        [
            ("PATH", Read("PATH")),
            ("GODOT_HOME", Read("GODOT_HOME"))
        ];

        static string? Read(string key)
        {
            try
            {
                return System.Environment.GetEnvironmentVariable(key, EnvironmentVariableTarget.User);
            }
            catch
            {
                return null;
            }
        }
    }

    private void RestorePersistentVars()
    {
        foreach (var (key, value) in _savedPersistentVars)
        {
            try
            {
                // Only write when a test actually changed it: restoring is a registry
                // write, and rewriting an untouched PATH on every fixture disposal
                // would be a needless (and broadcast-triggering) side effect of its own.
                if (System.Environment.GetEnvironmentVariable(key, EnvironmentVariableTarget.User) == value)
                {
                    continue;
                }

                System.Environment.SetEnvironmentVariable(key, value, EnvironmentVariableTarget.User);
            }
            catch
            {
                // Best effort: a locked-down or non-Windows host must not fail a test
                // run over cleanup of a variable it could not have written anyway.
            }
        }
    }
}
