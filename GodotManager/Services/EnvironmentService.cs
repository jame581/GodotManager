using System;
using System.IO;
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
        
        Environment.SetEnvironmentVariable(_paths.EnvVarName, entry.Path, target);

        var shimPath = Path.Combine(_paths.GetShimDirectory(entry.Scope), "godot.cmd");
        var exe = Path.Combine(entry.Path, "Godot.exe");
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

        var target = Array.Find(binaryCandidates, File.Exists) ?? entry.Path;
        var shimContent = $"#!/usr/bin/env bash\nsource \"{_paths.EnvScriptPath}\" 2>/dev/null\nexec \"{target}\" \"$@\"\n";
        File.WriteAllText(shimPath, shimContent);
        UnixFilePermissions.MakeExecutable(shimPath);
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
