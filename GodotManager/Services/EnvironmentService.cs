using System.Runtime.InteropServices;
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

        return Task.CompletedTask;
    }

    public async Task RemoveActiveAsync(InstallEntry? entry, CancellationToken cancellationToken = default)
    {
        if (OperatingSystem.IsWindows())
        {
            RemoveWindows(entry);
        }
        else
        {
            RemoveUnix();
        }

        await Task.CompletedTask;
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
        
        // Add shim directory to PATH
        var shimDir = _paths.GetShimDirectory(entry.Scope);
        AddToPath(shimDir, target);
        
        // Broadcast change notification to other processes (best effort)
        BroadcastEnvironmentChange();

        // Derive executable name from installation folder name
        // Folder name matches the archive binary name (e.g., Godot_v4.5.1-stable_win64.exe)
        var folderName = Path.GetFileName(entry.Path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        var expectedExe = folderName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
            ? folderName
            : folderName + ".exe";
        
        var exe = Path.Combine(entry.Path, expectedExe);
        
        // Fallback: search for any Godot executable if expected name not found
        if (!File.Exists(exe))
        {
            var files = Directory.GetFiles(entry.Path, "Godot*.exe", SearchOption.TopDirectoryOnly);
            exe = files.FirstOrDefault() ?? exe;
        }

        var shimPath = Path.Combine(_paths.GetShimDirectory(entry.Scope), "godot.cmd");
        var content = $"@echo off{Environment.NewLine}\"{exe}\" %*{Environment.NewLine}";
        File.WriteAllText(shimPath, content);

        // Create shortcuts if on Windows
        CreateShortcuts(entry, exe, createDesktopShortcut);
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
            catch
            {
                // Best effort
            }
        }

        // Delete shortcuts
        if (entry != null)
        {
            DeleteShortcuts(entry);
        }

        // Broadcast change notification
        BroadcastEnvironmentChange();
    }

    private void ApplyUnix(InstallEntry entry)
    {
        var exportLine = $"export {_paths.EnvVarName}=\"{entry.Path}\"";
        File.WriteAllText(_paths.EnvScriptPath, exportLine + Environment.NewLine);

        var shimPath = Path.Combine(_paths.GetShimDirectory(entry.Scope), "godot");
        
        // Derive binary name from installation folder name
        var folderName = Path.GetFileName(entry.Path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        var target = Path.Combine(entry.Path, folderName);
        
        // Fallback: search for any Godot binary if expected name not found
        if (!File.Exists(target))
        {
            try
            {
                var files = Directory.EnumerateFiles(entry.Path, "*", SearchOption.TopDirectoryOnly);
                var found = files.FirstOrDefault(f =>
                {
                    var name = Path.GetFileName(f);
                    return name.StartsWith("Godot", StringComparison.OrdinalIgnoreCase);
                });
                target = found ?? target;
            }
            catch
            {
                // ignore filesystem errors and use expected target
            }
        }

        var shimContent = $"#!/usr/bin/env bash\nsource \"{_paths.EnvScriptPath}\" 2>/dev/null\nexec \"{target}\" \"$@\"\n";
        File.WriteAllText(shimPath, shimContent);
        UnixFilePermissions.MakeExecutable(shimPath);
    }

    private void RemoveUnix()
    {
        // Remove environment script
        if (File.Exists(_paths.EnvScriptPath))
        {
            try
            {
                File.Delete(_paths.EnvScriptPath);
            }
            catch
            {
                // Best effort
            }
        }

        // Delete shim file
        var shimPath = Path.Combine(_paths.GetShimDirectory(InstallScope.User), "godot");
        if (File.Exists(shimPath))
        {
            try
            {
                File.Delete(shimPath);
            }
            catch
            {
                // Best effort
            }
        }
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

    private static void RemoveFromPath(string directory, EnvironmentVariableTarget target)
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
        catch
        {
            // PATH update is best-effort
        }
    }

    private void CreateShortcuts(InstallEntry entry, string exePath, bool createDesktopShortcut)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        try
        {
            var shortcutName = $"Godot {entry.Version} ({entry.Edition}).lnk";
            
            // Create Start Menu shortcut
            var startMenuFolder = entry.Scope == InstallScope.Global
                ? Environment.GetFolderPath(Environment.SpecialFolder.CommonStartMenu)
                : Environment.GetFolderPath(Environment.SpecialFolder.StartMenu);
            
            var godotManagerFolder = Path.Combine(startMenuFolder, "Programs", "GodotManager");
            Directory.CreateDirectory(godotManagerFolder);
            
            var startMenuShortcut = Path.Combine(godotManagerFolder, shortcutName);
            WindowsShortcut.Create(startMenuShortcut, exePath, entry.Path, $"Godot {entry.Version} ({entry.Edition})");
            
            // Create Desktop shortcut if requested
            if (createDesktopShortcut)
            {
                var desktopFolder = Environment.GetFolderPath(Environment.SpecialFolder.Desktop);
                var desktopShortcut = Path.Combine(desktopFolder, shortcutName);
                WindowsShortcut.Create(desktopShortcut, exePath, entry.Path, $"Godot {entry.Version} ({entry.Edition})");
            }
        }
        catch
        {
            // Shortcut creation is best-effort
        }
    }

    private void DeleteShortcuts(InstallEntry entry)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        try
        {
            var shortcutName = $"Godot {entry.Version} ({entry.Edition}).lnk";
            
            // Delete Start Menu shortcut
            var startMenuFolder = entry.Scope == InstallScope.Global
                ? Environment.GetFolderPath(Environment.SpecialFolder.CommonStartMenu)
                : Environment.GetFolderPath(Environment.SpecialFolder.StartMenu);
            
            var startMenuShortcut = Path.Combine(startMenuFolder, "Programs", "GodotManager", shortcutName);
            if (File.Exists(startMenuShortcut))
            {
                File.Delete(startMenuShortcut);
            }
            
            // Delete Desktop shortcut
            var desktopFolder = Environment.GetFolderPath(Environment.SpecialFolder.Desktop);
            var desktopShortcut = Path.Combine(desktopFolder, shortcutName);
            if (File.Exists(desktopShortcut))
            {
                File.Delete(desktopShortcut);
            }
        }
        catch
        {
            // Best effort
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
