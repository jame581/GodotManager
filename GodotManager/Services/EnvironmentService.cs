using GodotManager.Config;
using GodotManager.Domain;
using GodotManager.Infrastructure;
using System.Runtime.InteropServices;

namespace GodotManager.Services;

internal sealed class EnvironmentService
{
    private readonly AppPaths _paths;
    private readonly DiagnosticContext? _diagnostics;
    private readonly LauncherService _launcher;

    public EnvironmentService(AppPaths paths, DiagnosticContext? diagnostics = null, LauncherService? launcher = null)
    {
        _paths = paths;
        _diagnostics = diagnostics;
        _launcher = launcher ?? new LauncherService(paths, diagnostics);
    }

    /// <summary>
    /// Exposed so every front-end that already holds this service -- CLI, the hidden
    /// *-elevated mirrors, the TUI -- reaches the same launcher code without a second
    /// DI registration to keep in sync.
    /// </summary>
    public LauncherService Launcher => _launcher;

    public Task ApplyActiveAsync(InstallEntry entry, CancellationToken cancellationToken = default)
    {
        return ApplyActiveAsync(entry, dryRun: false, createDesktopShortcut: false, cancellationToken);
    }

    public Task ApplyActiveAsync(InstallEntry entry, bool dryRun, CancellationToken cancellationToken = default)
    {
        return ApplyActiveAsync(entry, dryRun, createDesktopShortcut: false, cancellationToken);
    }

    public Task ApplyActiveAsync(InstallEntry entry, bool dryRun, bool createDesktopShortcut, CancellationToken cancellationToken = default)
    {
        if (dryRun)
        {
            return Task.CompletedTask;
        }

        if (OperatingSystem.IsWindows())
        {
            ApplyWindows(entry, createDesktopShortcut);
        }
        else
        {
            ApplyUnix(entry);
        }

        // Rewritten on every activation, not just created when missing: that is what gives
        // pre-1.4.0 installs their entry, and it self-heals one whose target moved.
        if (entry.LauncherEntry != false)
        {
            _launcher.Create(entry);
        }

        return Task.CompletedTask;
    }

    /// <summary>
    /// The executable a shim written by this service launches: the quoted
    /// <c>exec "…"</c> target of the Unix shim, or the quoted <c>"…" %*</c> command of
    /// <c>godot.cmd</c>. Null when the content has neither shape (a hand-written or
    /// foreign file). Lives beside the writers so the two formats cannot drift apart
    /// unnoticed; <c>doctor</c> uses it to check what the shim really points at.
    /// </summary>
    internal static string? ParseShimTarget(string content)
    {
        foreach (var raw in content.Split('\n'))
        {
            var line = raw.Trim();
            string rest;
            if (line.StartsWith("exec \"", StringComparison.Ordinal))
            {
                rest = line["exec \"".Length..];
            }
            else if (line.StartsWith('"') && line.EndsWith("\" %*", StringComparison.Ordinal))
            {
                rest = line[1..];
            }
            else
            {
                continue;
            }

            var end = rest.IndexOf('"');
            if (end > 0)
            {
                return rest[..end];
            }
        }

        return null;
    }

    public Task RemoveActiveAsync(InstallEntry? entry, CancellationToken cancellationToken = default)
    {
        if (OperatingSystem.IsWindows())
        {
            RemoveWindows(entry);
        }
        else
        {
            RemoveUnix(entry);
        }

        return Task.CompletedTask;
    }

    private void ApplyWindows(InstallEntry entry, bool createDesktopShortcut)
    {
        var target = entry.Scope == InstallScope.Global
            ? EnvironmentVariableTarget.Machine
            : EnvironmentVariableTarget.User;

        // Set in registry for persistence
        Environment.SetEnvironmentVariable(_paths.EnvVarName, entry.Path, target);

        // Also set in current process so doctor command shows it immediately
        Environment.SetEnvironmentVariable(_paths.EnvVarName, entry.Path, EnvironmentVariableTarget.Process);

        // Ensure shim directory exists (may have been removed by clean command) and add to PATH
        var shimDir = _paths.GetShimDirectory(entry.Scope);
        Directory.CreateDirectory(shimDir);
        AddToPath(shimDir, target);

        // Broadcast change notification to other processes (best effort)
        BroadcastEnvironmentChange();

        // Standard builds: the binary is named after the install folder. .NET/mono
        // archives nest it one level down -- ResolveForInstall covers both.
        var exe = GodotExecutableLocator.ResolveForInstall(entry.Path, windows: true, _diagnostics);

        var shimPath = Path.Combine(shimDir, "godot.cmd");
        var content = $"@echo off{Environment.NewLine}\"{exe}\" %*{Environment.NewLine}";
        File.WriteAllText(shimPath, content);

        if (createDesktopShortcut)
        {
            _launcher.CreateDesktopShortcut(entry);
        }
    }

    private void RemoveWindows(InstallEntry? entry)
    {
        var target = entry?.Scope == InstallScope.Global
            ? EnvironmentVariableTarget.Machine
            : EnvironmentVariableTarget.User;

        // Remove GODOT_HOME environment variable
        Environment.SetEnvironmentVariable(_paths.EnvVarName, null, target);
        Environment.SetEnvironmentVariable(_paths.EnvVarName, null, EnvironmentVariableTarget.Process);

        // Remove shim directory from PATH
        var shimDir = _paths.GetShimDirectory(entry?.Scope ?? InstallScope.User);
        RemoveFromPath(shimDir, target);

        // Delete shim file
        var shimPath = Path.Combine(shimDir, "godot.cmd");
        if (File.Exists(shimPath))
        {
            try
            {
                File.Delete(shimPath);
            }
            catch (Exception ex)
            {
                _diagnostics?.Warn($"Failed to delete shim at {shimPath}: {ex.Message}");
            }
        }

        // Only the desktop shortcut belongs to the activation. The Start Menu entry
        // belongs to the install and is deleted by remove/clean, not by deactivation.
        if (entry != null)
        {
            _launcher.DeleteDesktopShortcut(entry);
        }

        // Broadcast change notification
        BroadcastEnvironmentChange();
    }

    private void ApplyUnix(InstallEntry entry)
    {
        // Ensure config directory exists (may have been removed by clean command)
        var envDir = Path.GetDirectoryName(_paths.EnvScriptPath);
        if (!string.IsNullOrEmpty(envDir))
        {
            Directory.CreateDirectory(envDir);
        }

        var exportLine = $"export {_paths.EnvVarName}=\"{entry.Path}\"";
        File.WriteAllText(_paths.EnvScriptPath, exportLine + Environment.NewLine);

        // Also set in current process so doctor command shows it immediately
        Environment.SetEnvironmentVariable(_paths.EnvVarName, entry.Path, EnvironmentVariableTarget.Process);

        // Ensure shim directory exists (may have been removed by clean command)
        var shimDir = _paths.GetShimDirectory(entry.Scope);
        Directory.CreateDirectory(shimDir);

        var shimPath = Path.Combine(shimDir, "godot");

        var target = GodotExecutableLocator.ResolveForInstall(entry.Path, windows: false, _diagnostics);

        var shimContent = $"#!/usr/bin/env bash\nsource \"{_paths.EnvScriptPath}\" 2>/dev/null\nexec \"{target}\" \"$@\"\n";
        File.WriteAllText(shimPath, shimContent);
        UnixFilePermissions.MakeExecutable(shimPath, _diagnostics);
    }

    private void RemoveUnix(InstallEntry? entry)
    {
        // Remove environment script
        if (File.Exists(_paths.EnvScriptPath))
        {
            try
            {
                File.Delete(_paths.EnvScriptPath);
            }
            catch (Exception ex)
            {
                _diagnostics?.Warn($"Failed to delete env script at {_paths.EnvScriptPath}: {ex.Message}");
            }
        }

        // Delete shim file from the correct scope directory
        var shimPath = Path.Combine(_paths.GetShimDirectory(entry?.Scope ?? InstallScope.User), "godot");
        if (File.Exists(shimPath))
        {
            try
            {
                File.Delete(shimPath);
            }
            catch (Exception ex)
            {
                _diagnostics?.Warn($"Failed to delete shim at {shimPath}: {ex.Message}");
            }
        }
    }

    private void AddToPath(string directory, EnvironmentVariableTarget target)
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
        catch (Exception ex)
        {
            _diagnostics?.Warn($"Failed to update PATH: {ex.Message}");
        }
    }

    private void RemoveFromPath(string directory, EnvironmentVariableTarget target)
    {
        try
        {
            var currentPath = Environment.GetEnvironmentVariable("PATH", target) ?? string.Empty;
            var paths = currentPath.Split(';', StringSplitOptions.RemoveEmptyEntries);
            var newPaths = paths.Where(p =>
                !string.Equals(p.Trim(), directory, StringComparison.OrdinalIgnoreCase));

            var newPath = string.Join(';', newPaths);
            Environment.SetEnvironmentVariable("PATH", newPath, target);

            // Also update current process PATH
            var processPath = Environment.GetEnvironmentVariable("PATH", EnvironmentVariableTarget.Process) ?? string.Empty;
            var processPaths = processPath.Split(';', StringSplitOptions.RemoveEmptyEntries);
            var newProcessPaths = processPaths.Where(p =>
                !string.Equals(p.Trim(), directory, StringComparison.OrdinalIgnoreCase));
            Environment.SetEnvironmentVariable("PATH", string.Join(';', newProcessPaths), EnvironmentVariableTarget.Process);
        }
        catch (Exception ex)
        {
            _diagnostics?.Warn($"Failed to clean PATH: {ex.Message}");
        }
    }

    private void BroadcastEnvironmentChange()
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
        catch (Exception ex)
        {
            _diagnostics?.Warn($"Failed to broadcast environment change: {ex.Message}");
        }
    }
}

internal static class UnixFilePermissions
{
    public static void MakeExecutable(string path, DiagnosticContext? diagnostics = null)
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
            diagnostics?.Warn("Filesystem does not support Unix permissions.");
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

internal static class WindowsShortcut
{
    public static void Create(string shortcutPath, string targetPath, string workingDirectory, string description)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var shell = (IShellLinkW)new ShellLink();

        shell.SetPath(targetPath);
        shell.SetWorkingDirectory(workingDirectory);
        shell.SetDescription(description);

        var persistFile = (IPersistFile)shell;
        persistFile.Save(shortcutPath, true);

        Marshal.ReleaseComObject(persistFile);
        Marshal.ReleaseComObject(shell);
    }

    [ComImport]
    [Guid("00021401-0000-0000-C000-000000000046")]
    [ClassInterface(ClassInterfaceType.None)]
    private class ShellLink { }

    [ComImport]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    [Guid("000214F9-0000-0000-C000-000000000046")]
    private interface IShellLinkW
    {
        void GetPath([Out, MarshalAs(UnmanagedType.LPWStr)] System.Text.StringBuilder pszFile, int cchMaxPath, IntPtr pfd, int fFlags);
        void GetIDList(out IntPtr ppidl);
        void SetIDList(IntPtr pidl);
        void GetDescription([Out, MarshalAs(UnmanagedType.LPWStr)] System.Text.StringBuilder pszName, int cchMaxName);
        void SetDescription([MarshalAs(UnmanagedType.LPWStr)] string pszName);
        void GetWorkingDirectory([Out, MarshalAs(UnmanagedType.LPWStr)] System.Text.StringBuilder pszDir, int cchMaxPath);
        void SetWorkingDirectory([MarshalAs(UnmanagedType.LPWStr)] string pszDir);
        void GetArguments([Out, MarshalAs(UnmanagedType.LPWStr)] System.Text.StringBuilder pszArgs, int cchMaxPath);
        void SetArguments([MarshalAs(UnmanagedType.LPWStr)] string pszArgs);
        void GetHotkey(out short pwHotkey);
        void SetHotkey(short wHotkey);
        void GetShowCmd(out int piShowCmd);
        void SetShowCmd(int iShowCmd);
        void GetIconLocation([Out, MarshalAs(UnmanagedType.LPWStr)] System.Text.StringBuilder pszIconPath, int cchIconPath, out int piIcon);
        void SetIconLocation([MarshalAs(UnmanagedType.LPWStr)] string pszIconPath, int iIcon);
        void SetRelativePath([MarshalAs(UnmanagedType.LPWStr)] string pszPathRel, int dwReserved);
        void Resolve(IntPtr hwnd, int fFlags);
        void SetPath([MarshalAs(UnmanagedType.LPWStr)] string pszFile);
    }

    [ComImport]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    [Guid("0000010b-0000-0000-C000-000000000046")]
    private interface IPersistFile
    {
        void GetClassID(out Guid pClassID);
        void IsDirty();
        void Load([MarshalAs(UnmanagedType.LPWStr)] string pszFileName, int dwMode);
        void Save([MarshalAs(UnmanagedType.LPWStr)] string pszFileName, bool fRemember);
        void SaveCompleted([MarshalAs(UnmanagedType.LPWStr)] string pszFileName);
        void GetCurFile([MarshalAs(UnmanagedType.LPWStr)] out string ppszFileName);
    }
}
