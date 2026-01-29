using System;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using GodotManager.Config;
using GodotManager.Domain;

namespace GodotManager.Services;

internal sealed class EnvironmentService
{
    private readonly AppPaths _paths;

    public EnvironmentService(AppPaths paths)
    {
        _paths = paths;
    }

    public Task ApplyActiveAsync(InstallEntry entry, CancellationToken cancellationToken = default)
    {
        return ApplyActiveAsync(entry, dryRun: false, cancellationToken);
    }

    public Task ApplyActiveAsync(InstallEntry entry, bool dryRun, CancellationToken cancellationToken = default)
    {
        if (dryRun)
        {
            return Task.CompletedTask;
        }

        if (OperatingSystem.IsWindows())
        {
            ApplyWindows(entry);
        }
        else
        {
            ApplyUnix(entry);
        }

        return Task.CompletedTask;
    }

    private void ApplyWindows(InstallEntry entry)
    {
        var target = entry.Scope == InstallScope.Global 
            ? EnvironmentVariableTarget.Machine 
            : EnvironmentVariableTarget.User;
        
        // Set in registry for persistence
        Environment.SetEnvironmentVariable(_paths.EnvVarName, entry.Path, target);
        
        // Also set in current process so doctor command shows it immediately
        Environment.SetEnvironmentVariable(_paths.EnvVarName, entry.Path, EnvironmentVariableTarget.Process);
        
        // Add shim directory to PATH
        var shimDir = _paths.GetShimDirectory(entry.Scope);
        AddToPath(shimDir, target);
        
        // Broadcast change notification to other processes (best effort)
        BroadcastEnvironmentChange();

        // Auto-detect Godot executable name
        var exeCandidates = new[]
        {
            Path.Combine(entry.Path, "Godot.exe"),
            Path.Combine(entry.Path, "Godot_v4.exe"),
            Path.Combine(entry.Path, "Godot_v3.exe")
        };

        // Search for known executable names first
        string? exe = Array.Find(exeCandidates, File.Exists);

        if (exe == null)
        {
            // Search for any .exe file that starts with "Godot" (e.g., Godot_v4.5.1-stable_win64.exe)
            var files = Directory.GetFiles(entry.Path, "Godot*.exe", SearchOption.TopDirectoryOnly);
            exe = files.FirstOrDefault() ?? Path.Combine(entry.Path, "Godot.exe");
        }

        var shimPath = Path.Combine(_paths.GetShimDirectory(entry.Scope), "godot.cmd");
        var content = $"@echo off{Environment.NewLine}\"{exe}\" %*{Environment.NewLine}";
        File.WriteAllText(shimPath, content);
    }

    private void ApplyUnix(InstallEntry entry)
    {
        var exportLine = $"export {_paths.EnvVarName}=\"{entry.Path}\"";
        File.WriteAllText(_paths.EnvScriptPath, exportLine + Environment.NewLine);

        var shimPath = Path.Combine(_paths.GetShimDirectory(entry.Scope), "godot");
        var binaryCandidates = new[]
        {
            Path.Combine(entry.Path, "godot"),
            Path.Combine(entry.Path, "Godot"),
            Path.Combine(entry.Path, "Godot_v4"),
            Path.Combine(entry.Path, "Godot_v3")
        };
        // Prefer known candidate names first, then try to find any file beginning with "Godot" or "godot"
        string? target = Array.Find(binaryCandidates, File.Exists);

        if (target == null)
        {
            try
            {
                var files = Directory.EnumerateFiles(entry.Path, "*", SearchOption.TopDirectoryOnly);
                target = files.FirstOrDefault(f =>
                {
                    var name = Path.GetFileName(f);
                    return name.StartsWith("Godot") || name.StartsWith("godot");
                });
            }
            catch
            {
                // ignore filesystem errors and fall back below
            }
        }

        // If we still didn't find a file, fall back to the conventional "godot" file inside the path
        target ??= Path.Combine(entry.Path, "godot");

        var shimContent = $"#!/usr/bin/env bash\nsource \"{_paths.EnvScriptPath}\" 2>/dev/null\nexec \"{target}\" \"$@\"\n";
        File.WriteAllText(shimPath, shimContent);
        UnixFilePermissions.MakeExecutable(shimPath);
    }

    private static void AddToPath(string directory, EnvironmentVariableTarget target)
    {
        try
        {
            var currentPath = Environment.GetEnvironmentVariable("PATH", target) ?? string.Empty;
            
            // Check if directory is already in PATH
            var paths = currentPath.Split(';', StringSplitOptions.RemoveEmptyEntries);
            var alreadyInPath = paths.Any(p => 
                string.Equals(p.Trim(), directory, StringComparison.OrdinalIgnoreCase));
            
            if (!alreadyInPath)
            {
                var newPath = string.IsNullOrEmpty(currentPath) 
                    ? directory 
                    : $"{currentPath};{directory}";
                
                Environment.SetEnvironmentVariable("PATH", newPath, target);
                
                // Also update current process PATH
                var processPath = Environment.GetEnvironmentVariable("PATH", EnvironmentVariableTarget.Process) ?? string.Empty;
                if (!processPath.Contains(directory, StringComparison.OrdinalIgnoreCase))
                {
                    Environment.SetEnvironmentVariable("PATH", $"{processPath};{directory}", EnvironmentVariableTarget.Process);
                }
            }
        }
        catch
        {
            // PATH update is best-effort; if it fails, shim will still be created
        }
    }

    private static void BroadcastEnvironmentChange()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        try
        {
            // Notify other processes about environment variable change
            // This is best effort - new processes will pick up the change
            const int HWND_BROADCAST = 0xffff;
            const int WM_SETTINGCHANGE = 0x1a;
            
            WindowsEnvironmentNotifier.SendMessageTimeout(
                new IntPtr(HWND_BROADCAST),
                WM_SETTINGCHANGE,
                IntPtr.Zero,
                "Environment",
                2, // SMTO_ABORTIFHUNG
                5000,
                out _);
        }
        catch
        {
            // Ignore failures - environment is still set in registry
        }
    }
}

internal static class UnixFilePermissions
{
    public static void MakeExecutable(string path)
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        try
        {
            var current = File.GetUnixFileMode(path);
            File.SetUnixFileMode(path, current | UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute);
        }
        catch (PlatformNotSupportedException)
        {
            // Ignore if FS does not support unix permissions.
        }
    }
}

internal static class WindowsEnvironmentNotifier
{
    [DllImport("user32.dll", EntryPoint = "SendMessageTimeoutW", CharSet = CharSet.Unicode, SetLastError = true)]
    internal static extern IntPtr SendMessageTimeout(
        IntPtr hWnd,
        int msg,
        IntPtr wParam,
        string lParam,
        int flags,
        int timeout,
        out IntPtr result);
}
