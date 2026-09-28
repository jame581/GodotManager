# Launcher Entries + 1.3.0 Follow-ups Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Give every installed Godot version its own application-launcher entry (GNOME/XDG `.desktop` on Linux, Start Menu `.lnk` on Windows) that is created on install and removed on remove/clean, and close the open 1.3.0 review follow-ups — all shipping in 1.4.0.

**Architecture:** A new `LauncherService` owns every launcher/shortcut file godman writes. It is owned by `EnvironmentService` (constructed there by default, exposed as `EnvironmentService.Launcher`) so every front-end — CLI, hidden `*-elevated` mirrors, TUI — reaches it through the service it already holds, with no DI changes. Paths come from new `AppPaths` accessors that derive from the same overrides (`GODMAN_HOME`, `GODMAN_GLOBAL_ROOT`) as everything else, so `GodmanTestFixture` isolates them for free.

**Tech Stack:** .NET 10, C#, Spectre.Console(.Cli), Terminal.Gui 2.4.17, xUnit.

**Spec:** `GodotManager/docs/superpowers/specs/2026-09-28-launcher-entries-and-1.3-followups-design.md`

## Global Constraints

- Branch: `fix/global-install-root-out-of-shim-dir`; version stays `1.4.0`.
- All user-facing output through Spectre.Console (`AnsiConsole.*`), never `Console.Write*` (a hook flags it).
- Best-effort file operations swallow failures and warn only via `DiagnosticContext.Warn` (shown under `--verbose`). Never `DiagnosticContext.WarnAlways` from a service — the TUI calls services while Terminal.Gui owns the screen.
- No new elevation predicates. Global-scope launcher writes happen only inside operations that are already elevated/sudo.
- Tests that touch the filesystem use `GodmanTestFixture`; CLI tests use `CliTestHarness.Create(fixture, …)`. Never hardcode real user paths.
- Linux `.desktop` file names start with `godman-godot-`; `clean` may glob only that prefix.
- Linux icon file name: `godman-godot.svg`; Godot logo is CC BY 4.0 (Andrea Calabró) — README must attribute it.
- Windows launcher/shortcut name stays `Godot <Version> (<Edition>).lnk` so 1.3.x-era shortcuts are found and deleted by name.
- Commit after each task. End every commit message with:
  `Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>`

## Deviations from the spec (decided while planning — spec updated to match)

1. **Registry guard (2.1):** instead of a per-load snapshot, `SaveAsync` rebases the *on-disk* global entries with the same `RebaseRelocatedInstallPaths` logic and compares serialized content. Same guarantee (a rebase alone never triggers a write), no new state on `InstallRegistry`.
2. **Test isolation:** no fixture change for `XDG_DATA_HOME`. On Linux `GODMAN_HOME` already substitutes `$HOME` wholesale, so when it is set it also wins over `XDG_DATA_HOME`. On Windows, with `GODMAN_HOME` set, the Start Menu and Desktop derive from it (`<appData>\Microsoft\Windows\Start Menu`, `<appData>\Desktop`) — the real Start Menu *is* `%APPDATA%\Microsoft\Windows\Start Menu`, so defaults are unchanged; tests stop writing into the developer's real Start Menu.
3. **`activate` rewrites** the launcher entry every time (unless opted out) instead of create-if-missing: cheap and self-healing if the executable path changed.
4. **Linux `.desktop` name includes the first 8 hex chars of the install Id** (`godman-godot-4.7.2-standard-user-1a2b3c4d.desktop`), so two installs of the same version/edition/scope at different `--path`s do not share one file.
5. **Remove deletes the launcher entry after the registry save succeeds**, so an unprivileged `remove` of a global install that fails on the registry write leaves the entry intact.
6. **2.2:** the cancel path already keeps the cache archive (`!cancellationToken.IsCancellationRequested` guard), so the new `bool` from `TryKillProcessTreeAsync` drives only a verbose warning.

## Review Focus

1. **Install path with spaces, quotes, `$`, backslash or `%`** — the `.desktop` `Exec=` line must still launch the right binary. Pinned by `BuildDesktopFile_EscapesExecPerDesktopEntrySpec` (Task 2).
2. **Developer machine exports `XDG_DATA_HOME`** — tests must never write into the real launcher. Pinned by `LauncherDirectory_UnderGodmanHomeOverride_IgnoresXdgDataHome` (Task 1).
3. **Unprivileged `remove` of a global install on Linux fails on the registry write** — the launcher entry must survive so the retry under sudo finds a consistent state. Pinned by `Remove_WhenRegistrySaveFails_KeepsLauncherEntry` (Task 5).
4. **Unprivileged user-scope save on an un-migrated machine** — must not attempt the global write just because an in-memory path was rebased. Pinned by `SaveAsync_RebaseOnly_DoesNotRewriteGlobalFile` (Task 3).
5. **`clean` in a shared `applications/` directory** — foreign `.desktop` files must survive. Pinned by `Clean_RemovesOnlyGodmanLauncherEntries` (Task 6).

---

### Task 1: Launcher locations on `AppPaths`

**Files:**
- Modify: `GodotManager/Config/AppPaths.cs`
- Test: `GodotManager.Tests/AppPathsTests.cs`

**Interfaces:**
- Produces:
  - `string AppPaths.GetLauncherDirectory(InstallScope scope)` — Linux: `<dataDir>/applications`; Windows: `<StartMenu>\Programs\godman`.
  - `string? AppPaths.GetLauncherIconPath(InstallScope scope)` — Linux: `<dataDir>/icons/hicolor/scalable/apps/godman-godot.svg`; Windows: `null`.
  - `string AppPaths.DesktopDirectory` — the per-user desktop (Windows desktop shortcut target).

- [ ] **Step 1: Write the failing tests** (append to `AppPathsTests`)

```csharp
[Fact]
public void LauncherDirectory_UnderGodmanHomeOverride_IgnoresXdgDataHome()
{
    var savedXdg = Environment.GetEnvironmentVariable("XDG_DATA_HOME");
    Environment.SetEnvironmentVariable("XDG_DATA_HOME", Path.Combine(Path.GetTempPath(), "must-not-be-used-" + Guid.NewGuid().ToString("N")));
    try
    {
        using var fixture = new GodmanTestFixture();

        foreach (var scope in new[] { InstallScope.User, InstallScope.Global })
        {
            Assert.StartsWith(fixture.TempRoot, fixture.Paths.GetLauncherDirectory(scope));
            var icon = fixture.Paths.GetLauncherIconPath(scope);
            if (icon is not null)
            {
                Assert.StartsWith(fixture.TempRoot, icon);
            }
        }

        Assert.StartsWith(fixture.TempRoot, fixture.Paths.DesktopDirectory);
    }
    finally
    {
        Environment.SetEnvironmentVariable("XDG_DATA_HOME", savedXdg);
    }
}

[Fact]
public void Linux_LauncherPaths_FollowXdgLayoutUnderTheOverrides()
{
    if (OperatingSystem.IsWindows())
    {
        return;
    }

    using var fixture = new GodmanTestFixture();
    var globalPrefix = Path.Combine(fixture.TempRoot, "global");

    Assert.Equal(Path.Combine(fixture.TempRoot, ".local", "share", "applications"),
        fixture.Paths.GetLauncherDirectory(InstallScope.User));
    Assert.Equal(Path.Combine(globalPrefix, "share", "applications"),
        fixture.Paths.GetLauncherDirectory(InstallScope.Global));
    Assert.Equal(Path.Combine(globalPrefix, "share", "icons", "hicolor", "scalable", "apps", "godman-godot.svg"),
        fixture.Paths.GetLauncherIconPath(InstallScope.Global));
}

[Fact]
public void Linux_DefaultGlobalLauncherDirectory_IsOnTheDefaultXdgDataDirs()
{
    if (OperatingSystem.IsWindows())
    {
        return;
    }

    // Constructed without overrides on purpose, like Linux_ScopePaths_KeepShimsInBinAndInstallsOutOfIt.
    var paths = new AppPaths();
    Assert.Equal("/usr/local/share/applications", paths.GetLauncherDirectory(InstallScope.Global));
}
```

- [ ] **Step 2: Run to verify they fail**

Run: `dotnet test --filter "FullyQualifiedName~AppPathsTests"`
Expected: build error — `GetLauncherDirectory` / `GetLauncherIconPath` / `DesktopDirectory` not defined.

- [ ] **Step 3: Implement**

In `AppPaths`, add fields next to the other `private readonly string` fields:

```csharp
private readonly string _userLauncherDirectory;
private readonly string _globalLauncherDirectory;
private readonly string? _userLauncherIconPath;
private readonly string? _globalLauncherIconPath;
```

and a public property next to `DownloadCacheDirectory`:

```csharp
public string DesktopDirectory { get; }
```

In the Windows branch, after `_globalConfigRoot = globalRoot;`:

```csharp
// The real Start Menu *is* %APPDATA%\Microsoft\Windows\Start Menu, so deriving it
// from appData changes nothing by default and keeps a GODMAN_HOME override --
// every test run -- out of the developer's real Start Menu.
var userStartMenu = overrideBase is null
    ? Environment.GetFolderPath(Environment.SpecialFolder.StartMenu)
    : System.IO.Path.Combine(appData, "Microsoft", "Windows", "Start Menu");
var globalStartMenu = overrideGlobalBase is null
    ? Environment.GetFolderPath(Environment.SpecialFolder.CommonStartMenu)
    : System.IO.Path.Combine(programFiles, "Microsoft", "Windows", "Start Menu");

_userLauncherDirectory = System.IO.Path.Combine(userStartMenu, "Programs", WindowsFolderName);
_globalLauncherDirectory = System.IO.Path.Combine(globalStartMenu, "Programs", WindowsFolderName);
_userLauncherIconPath = null;   // .lnk files take the icon from the executable
_globalLauncherIconPath = null;
DesktopDirectory = overrideBase is null
    ? Environment.GetFolderPath(Environment.SpecialFolder.Desktop)
    : System.IO.Path.Combine(appData, "Desktop");
```

In the Linux branch, after `_globalConfigRoot = globalInstallRoot;`:

```csharp
// GODMAN_HOME stands in for $HOME wholesale on Linux (the shim and installs already
// resolve under it), so an override also wins over XDG_DATA_HOME -- otherwise a test
// run on a machine that exports XDG_DATA_HOME would write into the real launcher.
var xdgDataHome = Environment.GetEnvironmentVariable("XDG_DATA_HOME");
var userDataDir = overrideBase is null && !string.IsNullOrEmpty(xdgDataHome) && System.IO.Path.IsPathRooted(xdgDataHome)
    ? xdgDataHome
    : System.IO.Path.Combine(home, ".local", "share");
// <prefix>/share is on the default XDG_DATA_DIRS (/usr/local/share:/usr/share).
var globalDataDir = System.IO.Path.Combine(globalPrefix, "share");

_userLauncherDirectory = System.IO.Path.Combine(userDataDir, "applications");
_globalLauncherDirectory = System.IO.Path.Combine(globalDataDir, "applications");
_userLauncherIconPath = LauncherIconPath(userDataDir);
_globalLauncherIconPath = LauncherIconPath(globalDataDir);
DesktopDirectory = System.IO.Path.Combine(home, "Desktop");
```

Add the helper and accessors next to `GetShimDirectory`:

```csharp
private static string LauncherIconPath(string dataDir) =>
    System.IO.Path.Combine(dataDir, "icons", "hicolor", "scalable", "apps", "godman-godot.svg");

/// <summary>
/// Where godman writes each install's launcher entry: an XDG <c>applications/</c>
/// directory on Linux, godman's own Start Menu folder on Windows. Not created here --
/// <see cref="Services.LauncherService"/> creates it on first write, since the global
/// one needs privileges most runs do not have.
/// </summary>
public string GetLauncherDirectory(InstallScope scope)
{
    return scope == InstallScope.Global ? _globalLauncherDirectory : _userLauncherDirectory;
}

/// <summary>The icon the Linux <c>.desktop</c> entries point at; null on Windows.</summary>
public string? GetLauncherIconPath(InstallScope scope)
{
    return scope == InstallScope.Global ? _globalLauncherIconPath : _userLauncherIconPath;
}
```

Do **not** add these directories to `EnsureDirectories()`.

- [ ] **Step 4: Run to verify they pass**

Run: `dotnet test --filter "FullyQualifiedName~AppPathsTests"`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add GodotManager/Config/AppPaths.cs GodotManager.Tests/AppPathsTests.cs
git commit -m "feat(paths): resolve launcher entry locations per scope

Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```

---

### Task 2: `LauncherService` + icon asset + shared executable resolution

**Files:**
- Create: `GodotManager/Services/LauncherService.cs`
- Create: `GodotManager/Assets/godot-icon.svg`
- Modify: `GodotManager/GodotManager.csproj` (embedded resource)
- Modify: `GodotManager/Services/GodotExecutableLocator.cs` (add `ResolveForInstall`)
- Modify: `GodotManager/Services/EnvironmentService.cs` (use `ResolveForInstall`; own a `LauncherService`; drop `CreateShortcuts`/`DeleteShortcuts`)
- Modify: `GodotManager.Tests/Helpers/GodmanTestFixture.cs` (expose `Launcher`)
- Test: `GodotManager.Tests/LauncherServiceTests.cs`, `GodotManager.Tests/GodotExecutableLocatorTests.cs`

**Interfaces:**
- Consumes: Task 1 accessors.
- Produces:
  - `static string GodotExecutableLocator.ResolveForInstall(string installPath, bool windows, DiagnosticContext? diagnostics = null)` — folder-name guess, then `Find`, then the guess again as the fallback.
  - `sealed class LauncherService(AppPaths paths, DiagnosticContext? diagnostics = null)` with:
    - `void Create(InstallEntry entry)`
    - `void CreateDesktopShortcut(InstallEntry entry)` (no-op off Windows)
    - `void Delete(InstallEntry entry)` (launcher entry + desktop shortcut)
    - `void DeleteDesktopShortcut(InstallEntry entry)` (no-op off Windows)
    - `bool Exists(InstallEntry entry)`
    - `IReadOnlyList<string> DeleteAll(InstallScope scope, IEnumerable<InstallEntry> installs)` — returns removed paths
    - `internal static string DisplayName(InstallEntry entry)`
    - `internal static string BuildDesktopFileName(InstallEntry entry)`
    - `internal static string GetEntryPath(InstallEntry entry, AppPaths paths)`
    - `internal static string BuildDesktopFile(InstallEntry entry, string exePath, string iconPath)`
  - `EnvironmentService(AppPaths paths, DiagnosticContext? diagnostics = null, LauncherService? launcher = null)` and `LauncherService EnvironmentService.Launcher { get; }`
  - `LauncherService GodmanTestFixture.Launcher { get; }`

- [ ] **Step 1: Add the icon asset**

```bash
mkdir -p GodotManager/Assets
curl -fsSL https://raw.githubusercontent.com/godotengine/godot/master/icon.svg -o GodotManager/Assets/godot-icon.svg
head -c 200 GodotManager/Assets/godot-icon.svg   # must start with <svg
```

In `GodotManager/GodotManager.csproj`, add an `ItemGroup`:

```xml
<ItemGroup>
  <!-- Godot logo by Andrea Calabró, CC BY 4.0 -- attributed in README. -->
  <EmbeddedResource Include="Assets\godot-icon.svg" LogicalName="GodotManager.Assets.godot-icon.svg" />
</ItemGroup>
```

- [ ] **Step 2: Write the failing tests** — create `GodotManager.Tests/LauncherServiceTests.cs`

```csharp
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
        Assert.Contains("\nExec=\"/opt/godot/Godot\" %f\n", text);
        Assert.Contains("\nIcon=/icons/godman-godot.svg\n", text);
        Assert.Contains("\nTerminal=false\n", text);
        Assert.Contains("\nStartupWMClass=Godot\n", text);
    }

    [Fact]
    public void BuildDesktopFile_EscapesExecPerDesktopEntrySpec()
    {
        // Quoting rule: ", `, $ and \ inside the quoted argument get a backslash. Then the
        // general string-escape rule doubles every backslash, and a literal % is doubled
        // so it is not read as a field code. A space needs nothing beyond the quotes.
        var entry = InstallEntryFactory.Create();

        var text = LauncherService.BuildDesktopFile(entry, """/opt/my godot/a"b$c\d%e""", "/i.svg");

        Assert.Contains("""Exec="/opt/my godot/a\\"b\\$c\\\\d%%e" %f""", text);
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
        Assert.Contains($"Exec=\"{binary}\" %f", File.ReadAllText(entryPath));

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
```

Add to `GodotExecutableLocatorTests.cs`:

```csharp
[Fact]
public void ResolveForInstall_PrefersTheFolderNamedBinary()
{
    var root = Path.Combine(Path.GetTempPath(), "godman-locator-" + Guid.NewGuid().ToString("N"), "Godot_v4.7.2-stable_linux.x86_64");
    Directory.CreateDirectory(root);
    try
    {
        var expected = Path.Combine(root, "Godot_v4.7.2-stable_linux.x86_64");
        File.WriteAllText(expected, "x");

        Assert.Equal(expected, GodotExecutableLocator.ResolveForInstall(root, windows: false));
    }
    finally
    {
        Directory.Delete(Path.GetDirectoryName(root)!, recursive: true);
    }
}

[Fact]
public void ResolveForInstall_WithNothingOnDisk_FallsBackToTheFolderNameGuess()
{
    var root = Path.Combine(Path.GetTempPath(), "does-not-exist-" + Guid.NewGuid().ToString("N"), "Godot_v4.7.2-stable_win64.exe");

    Assert.Equal(Path.Combine(root, "Godot_v4.7.2-stable_win64.exe"), GodotExecutableLocator.ResolveForInstall(root, windows: true));
}
```

- [ ] **Step 3: Run to verify they fail**

Run: `dotnet test --filter "FullyQualifiedName~LauncherServiceTests|FullyQualifiedName~GodotExecutableLocatorTests"`
Expected: build errors — `LauncherService`, `ResolveForInstall`, `GodmanTestFixture.Launcher` not defined.

- [ ] **Step 4: Implement `ResolveForInstall`** in `GodotExecutableLocator`

```csharp
/// <summary>
/// The binary a shim or launcher entry should point at. The folder-name guess is
/// right for standard builds; <see cref="Find"/> covers the nested .NET layout. When
/// neither finds anything the guess is returned anyway, so a caller always gets a
/// path -- the same behaviour the shims have always had.
/// </summary>
public static string ResolveForInstall(string installPath, bool windows, DiagnosticContext? diagnostics = null)
{
    var folderName = Path.GetFileName(installPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
    var expected = windows && !folderName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
        ? folderName + ".exe"
        : folderName;
    var guess = Path.Combine(installPath, expected);

    if (File.Exists(guess))
    {
        return guess;
    }

    return Find(installPath, windows, diagnostics) ?? guess;
}
```

Then in `EnvironmentService.ApplyWindows` replace the block from `// Derive executable name from installation folder name` through the `if (!File.Exists(exe)) { … }` block with:

```csharp
// Standard builds: the binary is named after the install folder. .NET/mono
// archives nest it one level down -- ResolveForInstall covers both.
var exe = GodotExecutableLocator.ResolveForInstall(entry.Path, windows: true, _diagnostics);
```

and in `ApplyUnix` replace the `folderName`/`target`/fallback block with:

```csharp
var target = GodotExecutableLocator.ResolveForInstall(entry.Path, windows: false, _diagnostics);
```

- [ ] **Step 5: Implement `LauncherService`** — create `GodotManager/Services/LauncherService.cs`

```csharp
using GodotManager.Config;
using GodotManager.Domain;
using GodotManager.Infrastructure;
using System.Text;

namespace GodotManager.Services;

/// <summary>
/// Owns every application-launcher file godman writes for an install: an XDG
/// <c>.desktop</c> entry on Linux, a Start Menu <c>.lnk</c> on Windows, plus the
/// optional Windows desktop shortcut. One entry per installed version, created on
/// install and deleted on remove/clean.
///
/// Everything here is best-effort, like shim cleanup: a failure is a verbose
/// <c>warn:</c>, never an exception, because a missing launcher entry must not fail
/// an install or strand a removal half-done. And never <c>WarnAlways</c> -- the TUI
/// calls this while Terminal.Gui owns the screen.
/// </summary>
internal sealed class LauncherService
{
    internal const string DesktopFilePrefix = "godman-godot-";
    private const string IconResourceName = "GodotManager.Assets.godot-icon.svg";

    private readonly AppPaths _paths;
    private readonly DiagnosticContext? _diagnostics;

    public LauncherService(AppPaths paths, DiagnosticContext? diagnostics = null)
    {
        _paths = paths;
        _diagnostics = diagnostics;
    }

    public void Create(InstallEntry entry)
    {
        try
        {
            var windows = OperatingSystem.IsWindows();
            var exe = GodotExecutableLocator.ResolveForInstall(entry.Path, windows, _diagnostics);
            var entryPath = GetEntryPath(entry, _paths);
            Directory.CreateDirectory(Path.GetDirectoryName(entryPath)!);

            if (windows)
            {
                WindowsShortcut.Create(entryPath, exe, entry.Path, DisplayName(entry));
                return;
            }

            var iconPath = _paths.GetLauncherIconPath(entry.Scope)!;
            WriteIcon(iconPath);
            File.WriteAllText(entryPath, BuildDesktopFile(entry, exe, iconPath));
        }
        catch (Exception ex)
        {
            _diagnostics?.Warn($"Failed to create launcher entry for {entry.Version}: {ex.Message}");
        }
    }

    public void CreateDesktopShortcut(InstallEntry entry)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        try
        {
            var exe = GodotExecutableLocator.ResolveForInstall(entry.Path, windows: true, _diagnostics);
            Directory.CreateDirectory(_paths.DesktopDirectory);
            WindowsShortcut.Create(GetDesktopShortcutPath(entry, _paths), exe, entry.Path, DisplayName(entry));
        }
        catch (Exception ex)
        {
            _diagnostics?.Warn($"Failed to create desktop shortcut for {entry.Version}: {ex.Message}");
        }
    }

    public void Delete(InstallEntry entry)
    {
        TryDelete(GetEntryPath(entry, _paths));
        DeleteDesktopShortcut(entry);
    }

    public void DeleteDesktopShortcut(InstallEntry entry)
    {
        if (OperatingSystem.IsWindows())
        {
            TryDelete(GetDesktopShortcutPath(entry, _paths));
        }
    }

    public bool Exists(InstallEntry entry) => File.Exists(GetEntryPath(entry, _paths));

    /// <summary>
    /// Removes every launcher file godman owns in <paramref name="scope"/>. On Linux the
    /// <c>applications/</c> directory is shared with every other app, so only files with
    /// godman's prefix are touched. On Windows the Start Menu folder is godman's own and
    /// goes whole; the desktop is not ours, so only the exact names of
    /// <paramref name="installs"/> are deleted there.
    /// </summary>
    public IReadOnlyList<string> DeleteAll(InstallScope scope, IEnumerable<InstallEntry> installs)
    {
        var removed = new List<string>();
        var dir = _paths.GetLauncherDirectory(scope);

        try
        {
            if (OperatingSystem.IsWindows())
            {
                if (Directory.Exists(dir))
                {
                    Directory.Delete(dir, recursive: true);
                    removed.Add(dir);
                }

                foreach (var entry in installs.Where(x => x.Scope == scope))
                {
                    var shortcut = GetDesktopShortcutPath(entry, _paths);
                    if (TryDelete(shortcut))
                    {
                        removed.Add(shortcut);
                    }
                }
            }
            else
            {
                if (Directory.Exists(dir))
                {
                    foreach (var file in Directory.EnumerateFiles(dir, DesktopFilePrefix + "*.desktop").ToList())
                    {
                        if (TryDelete(file))
                        {
                            removed.Add(file);
                        }
                    }
                }

                var icon = _paths.GetLauncherIconPath(scope);
                if (icon is not null && TryDelete(icon))
                {
                    removed.Add(icon);
                }
            }
        }
        catch (Exception ex)
        {
            _diagnostics?.Warn($"Failed to clean launcher entries in {dir}: {ex.Message}");
        }

        return removed;
    }

    internal static string DisplayName(InstallEntry entry) => $"Godot {entry.Version} ({entry.Edition})";

    /// <summary>
    /// Lowercase, <c>[a-z0-9.-]</c> only, and carries the first 8 hex digits of the Id so
    /// two installs of one version at different <c>--path</c>s never share a file.
    /// </summary>
    internal static string BuildDesktopFileName(InstallEntry entry)
    {
        var raw = $"{DesktopFilePrefix}{entry.Version}-{entry.Edition}-{entry.Scope}-{entry.Id.ToString("N")[..8]}".ToLowerInvariant();
        var builder = new StringBuilder(raw.Length + 8);
        foreach (var c in raw)
        {
            builder.Append(c is (>= 'a' and <= 'z') or (>= '0' and <= '9') or '.' or '-' ? c : '-');
        }

        return builder.Append(".desktop").ToString();
    }

    internal static string GetEntryPath(InstallEntry entry, AppPaths paths) =>
        Path.Combine(
            paths.GetLauncherDirectory(entry.Scope),
            OperatingSystem.IsWindows() ? DisplayName(entry) + ".lnk" : BuildDesktopFileName(entry));

    private static string GetDesktopShortcutPath(InstallEntry entry, AppPaths paths) =>
        Path.Combine(paths.DesktopDirectory, DisplayName(entry) + ".lnk");

    internal static string BuildDesktopFile(InstallEntry entry, string exePath, string iconPath)
    {
        var builder = new StringBuilder();
        builder.Append("[Desktop Entry]\n");
        builder.Append("Type=Application\n");
        builder.Append("Name=").Append(EscapeValue(DisplayName(entry))).Append('\n');
        builder.Append("Comment=Godot Engine editor, managed by godman\n");
        builder.Append("Exec=").Append(QuoteExecArgument(exePath)).Append(" %f\n");
        builder.Append("Icon=").Append(EscapeValue(iconPath)).Append('\n');
        builder.Append("Terminal=false\n");
        builder.Append("Categories=Development;IDE;\n");
        // Godot's X11/Wayland window class; lets the shell group the running editor
        // under this entry instead of showing an anonymous window.
        builder.Append("StartupWMClass=Godot\n");
        return builder.ToString();
    }

    /// <summary>
    /// Desktop Entry spec, "The Exec key": the argument is double-quoted with ", `, $
    /// and \ backslash-escaped inside; a literal % is doubled so it is not read as a
    /// field code; and the general string-escape rule is applied on top, which is why
    /// a literal backslash ends up as four.
    /// </summary>
    private static string QuoteExecArgument(string argument)
    {
        var quoted = new StringBuilder("\"");
        foreach (var c in argument)
        {
            if (c is '"' or '`' or '$' or '\\')
            {
                quoted.Append('\\');
            }

            quoted.Append(c);
        }

        quoted.Append('"');
        return EscapeValue(quoted.ToString().Replace("%", "%%"));
    }

    private static string EscapeValue(string value) =>
        value.Replace("\\", "\\\\").Replace("\n", "\\n").Replace("\r", "\\r").Replace("\t", "\\t");

    private static void WriteIcon(string iconPath)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(iconPath)!);
        using var resource = typeof(LauncherService).Assembly.GetManifestResourceStream(IconResourceName)
            ?? throw new InvalidOperationException($"Embedded resource {IconResourceName} is missing.");
        using var file = File.Create(iconPath);
        resource.CopyTo(file);
    }

    private bool TryDelete(string path)
    {
        try
        {
            if (!File.Exists(path))
            {
                return false;
            }

            File.Delete(path);
            return true;
        }
        catch (Exception ex)
        {
            _diagnostics?.Warn($"Failed to delete {path}: {ex.Message}");
            return false;
        }
    }
}
```

- [ ] **Step 6: Wire `LauncherService` into `EnvironmentService` and the fixture**

`EnvironmentService`: replace the constructor and add the property:

```csharp
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
```

For this task keep behaviour identical: in `ApplyWindows` replace `CreateShortcuts(entry, exe, createDesktopShortcut);` with

```csharp
_launcher.Create(entry);
if (createDesktopShortcut)
{
    _launcher.CreateDesktopShortcut(entry);
}
```

in `RemoveWindows` replace the `DeleteShortcuts(entry)` call with `_launcher.Delete(entry);`, and delete the now-unused private `CreateShortcuts` and `DeleteShortcuts` methods. (`WindowsShortcut` stays in `EnvironmentService.cs`.)

`GodmanTestFixture`: add `public LauncherService Launcher { get; }` and replace the `Environment = …` line with:

```csharp
Launcher = new LauncherService(Paths);
Environment = new EnvironmentService(Paths, diagnostics: null, Launcher);
```

- [ ] **Step 7: Run to verify they pass, then the full suite**

Run: `dotnet test --filter "FullyQualifiedName~LauncherServiceTests|FullyQualifiedName~GodotExecutableLocatorTests"` → PASS
Run: `dotnet test -v minimal` → all pass (306+ passed, 0 failed).

- [ ] **Step 8: Commit**

```bash
git add GodotManager/Assets GodotManager/GodotManager.csproj GodotManager/Services GodotManager.Tests
git commit -m "feat(launcher): add LauncherService for per-install launcher entries

Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```

---

### Task 3: Global registry write guard compares content (follow-up 2.1)

**Files:**
- Modify: `GodotManager/Services/RegistryService.cs` (`SaveAsync`, `RebaseRelocatedInstallPaths` doc comment)
- Test: `GodotManager.Tests/RegistryServicePathRelocationTests.cs`, `GodotManager.Tests/RegistryServiceTests.cs`

**Interfaces:**
- Consumes: nothing new.
- Produces: `SaveAsync` persists in-place field changes to global entries (Task 4's `LauncherEntry` relies on this for any future mutation).

- [ ] **Step 1: Note the existing helpers** in `RegistryServicePathRelocationTests.cs`: `FirstRelocation()` returns the `(OldRoot, NewRoot)` pair whose `NewRoot` is the fixture's global install root, and `WriteGlobalFileAsync(entry)` writes a raw global `installs.json` at `_fixture.Paths.GlobalRegistryFile`. The new relocation test uses both.

- [ ] **Step 2: Write the failing tests**

In `RegistryServiceTests.cs`:

```csharp
[Fact]
public async Task SaveAsync_InPlaceFieldChangeOnGlobalEntry_IsPersisted()
{
    using var fixture = new GodmanTestFixture();
    var installPath = Path.Combine(fixture.TempRoot, "global-install");
    Directory.CreateDirectory(installPath);
    var entry = InstallEntryFactory.Create(scope: InstallScope.Global, path: installPath);
    await fixture.Registry.SaveAsync(new InstallRegistry { Installs = [entry] });

    var loaded = await fixture.Registry.LoadAsync();
    loaded.Installs.Single().ChecksumVerified = true;   // same Id set, different content
    await fixture.Registry.SaveAsync(loaded);

    var reloaded = await fixture.Registry.LoadAsync();
    Assert.True(reloaded.Installs.Single().ChecksumVerified);
}
```

In `RegistryServicePathRelocationTests.cs`:

```csharp
[Fact]
public async Task SaveAsync_RebaseOnly_DoesNotRewriteGlobalFile()
{
    // The state of an un-migrated machine as the registry sees it: the global file
    // records the old-root path, the files exist only under the new root, so LoadAsync
    // rebases the in-memory path. A content comparison against the raw file would call
    // that a change and make every unprivileged save try to write the global file.
    var (oldRoot, newRoot) = FirstRelocation();
    var oldPath = Path.Combine(oldRoot, "4.6.2-standard-linux-global");
    Directory.CreateDirectory(Path.Combine(newRoot, "4.6.2-standard-linux-global"));
    await WriteGlobalFileAsync(InstallEntryFactory.Create(
        version: "4.6.2", scope: InstallScope.Global, path: oldPath));

    var globalFile = _fixture.Paths.GlobalRegistryFile;
    var before = await File.ReadAllTextAsync(globalFile);
    var writeTimeBefore = File.GetLastWriteTimeUtc(globalFile);

    var loaded = await _fixture.Registry.LoadAsync();
    Assert.NotEqual(oldPath, Assert.Single(loaded.Installs).Path); // arrange: the rebase happened
    await _fixture.Registry.SaveAsync(loaded);

    Assert.Equal(before, await File.ReadAllTextAsync(globalFile));
    Assert.Equal(writeTimeBefore, File.GetLastWriteTimeUtc(globalFile));
}
```

- [ ] **Step 3: Run to verify**

Run: `dotnet test --filter "FullyQualifiedName~SaveAsync_InPlaceFieldChangeOnGlobalEntry_IsPersisted|FullyQualifiedName~SaveAsync_RebaseOnly_DoesNotRewriteGlobalFile"`
Expected: `InPlaceFieldChange` FAILS (`ChecksumVerified` is false after reload). `RebaseOnly` PASSES today — it is the guard against the naive fix; record that it passed before the change.

- [ ] **Step 4: Implement** — in `SaveAsync`, directly after `var currentGlobal = await LoadGlobalBestEffortAsync(cancellationToken);` add:

```csharp
// Rebase the on-disk copy exactly as LoadAsync rebased the caller's copy. The
// content comparison further down must see a path the migration already accounts
// for as unchanged -- otherwise every unprivileged save on an un-migrated machine
// would try, and fail, to rewrite the machine-wide file.
RebaseRelocatedInstallPaths(currentGlobal);
```

Replace the guard

```csharp
var desiredGlobalIds = desiredGlobal.Select(x => x.Id).ToHashSet();

if (!currentGlobalIds.SetEquals(desiredGlobalIds))
```

with

```csharp
if (!GlobalEntriesEquivalent(currentGlobal.Installs, desiredGlobal))
```

and update the comment above it to say "only when the legitimately global entries actually changed — by Id set or by content". Add:

```csharp
/// <summary>
/// Same Ids and the same serialized content per Id. Compares content, not just Ids,
/// so an in-place field change on a global entry is persisted; both sides are
/// rebased first (see <see cref="SaveAsync"/>) so a migration-only path difference
/// is not a change.
/// </summary>
private bool GlobalEntriesEquivalent(List<InstallEntry> current, List<InstallEntry> desired)
{
    if (current.Count != desired.Count)
    {
        return false;
    }

    var currentById = current.ToDictionary(x => x.Id, x => JsonSerializer.Serialize(x, _jsonOptions));
    foreach (var entry in desired)
    {
        if (!currentById.TryGetValue(entry.Id, out var serialized)
            || serialized != JsonSerializer.Serialize(entry, _jsonOptions))
        {
            return false;
        }
    }

    return true;
}
```

`currentGlobalIds` is still used by `IsStrayInUserFile`; keep it (compute it after the rebase — Ids do not change). `RebaseRelocatedInstallPaths` emits a verbose `warn:` per rebased entry, so calling it here repeats those lines under `--verbose` on an un-migrated machine; pass a `bool quiet` parameter (default `false`) and call it with `quiet: true` from `SaveAsync`. In the `RebaseRelocatedInstallPaths` doc comment, replace the parenthetical "(… the global write is guarded on the *set of Ids* changing, which a path-only correction does not change.)" with "(… the global write compares against the on-disk entries rebased the same way, so a path-only correction is not a change.)". Update CLAUDE.md's sentence "note the global write is guarded on the Id set changing, which a path-only fix does not" to "the global write compares content against the on-disk entries rebased the same way, so a path-only fix never triggers it".

- [ ] **Step 5: Run** the two tests (both PASS), then `dotnet test -v minimal` (all pass).

- [ ] **Step 6: Mutation check (observed, not predicted)** — temporarily delete the `RebaseRelocatedInstallPaths(currentGlobal);` line, run `SaveAsync_RebaseOnly_DoesNotRewriteGlobalFile`, confirm it FAILS, restore the line. Report the observed result in the task report.

- [ ] **Step 7: Commit**

```bash
git add GodotManager/Services/RegistryService.cs GodotManager.Tests CLAUDE.md
git commit -m "fix(registry): persist in-place changes to global entries

Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```

---

### Task 4: `LauncherEntry` field, `--no-shortcut`, install/activate lifecycle, `install --activate` cleanup

**Files:**
- Modify: `GodotManager/Domain/InstallEntry.cs`
- Modify: `GodotManager/Services/InstallerService.cs` (`InstallRequest`, `ElevatedInstallPayload`, `BuildElevatedPayload`, `InstallAsync`, `DryRunInstallAsync`)
- Modify: `GodotManager/Commands/InstallCommand.cs` (option, request, preview)
- Modify: `GodotManager/Commands/ElevatedInstallCommand.cs` (`BuildRequest`)
- Modify: `GodotManager/Services/EnvironmentService.cs` (`ApplyActiveAsync`, `RemoveWindows`)
- Test: `GodotManager.Tests/E2E/LauncherLifecycleE2ETests.cs` (new), `GodotManager.Tests/InstallerServiceIntegrationTests.cs`

**Interfaces:**
- Consumes: `EnvironmentService.Launcher` (Task 2).
- Produces:
  - `bool? InstallEntry.LauncherEntry` — null = pre-1.4.0 (wanted), true = created, false = opted out.
  - `InstallRequest(..., bool CreateLauncherEntry = true)` — new **last** positional parameter.
  - `ElevatedInstallPayload(..., bool CreateLauncherEntry = true)` — new last parameter.
  - `InstallCommand.Settings.NoShortcut` (`--no-shortcut`).
  - Semantics: `ApplyActiveAsync` rewrites the launcher entry unless `LauncherEntry == false`; `RemoveActiveAsync` deletes only the Windows desktop shortcut.

- [ ] **Step 1: Write the failing E2E tests** — create `GodotManager.Tests/E2E/LauncherLifecycleE2ETests.cs`

```csharp
using GodotManager.Domain;
using GodotManager.Services;
using GodotManager.Tests.Helpers;
using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Xunit;

namespace GodotManager.Tests.E2E;

public class LauncherLifecycleE2ETests : IDisposable
{
    private readonly GodmanTestFixture _fixture = new();
    public void Dispose() => _fixture.Dispose();

    private static string Platform => OperatingSystem.IsWindows() ? "windows" : "linux";

    private async Task<InstallEntry> InstallAsync(string version, params string[] extra)
    {
        var archive = MockArchiveFactory.CreateMockGodotArchive();
        try
        {
            var app = CliTestHarness.Create(_fixture);
            var result = await app.RunAsync(["install", "--version", version, "--archive", archive, "--platform", Platform, .. extra]);
            Assert.Equal(0, result.ExitCode);
        }
        finally
        {
            File.Delete(archive);
        }

        var registry = await _fixture.Registry.LoadAsync();
        return registry.Installs.Single(x => x.Version == version);
    }

    [Fact]
    public async Task Install_CreatesLauncherEntry_AndRecordsIt()
    {
        if (OperatingSystem.IsWindows()) return; // .lnk creation is manual-test territory
        var entry = await InstallAsync("4.5.1");

        Assert.True(_fixture.Launcher.Exists(entry));
        Assert.True(entry.LauncherEntry);
    }

    [Fact]
    public async Task Install_NoShortcut_CreatesNothing_AndRecordsTheOptOut()
    {
        var entry = await InstallAsync("4.5.1", "--no-shortcut");

        Assert.False(_fixture.Launcher.Exists(entry));
        Assert.False(entry.LauncherEntry);
    }

    [Fact]
    public async Task Activate_LegacyEntry_BackfillsLauncherEntry()
    {
        if (OperatingSystem.IsWindows()) return;
        var entry = await InstallAsync("4.5.1", "--no-shortcut");
        var registry = await _fixture.Registry.LoadAsync();
        registry.Installs.Single().LauncherEntry = null;   // what a pre-1.4.0 registry holds
        await _fixture.Registry.SaveAsync(registry);

        var result = await CliTestHarness.Create(_fixture).RunAsync(["activate", entry.Id.ToString()]);

        Assert.Equal(0, result.ExitCode);
        Assert.True(_fixture.Launcher.Exists(entry));
    }

    [Fact]
    public async Task Activate_OptedOutEntry_DoesNotCreateLauncherEntry()
    {
        var entry = await InstallAsync("4.5.1", "--no-shortcut");

        var result = await CliTestHarness.Create(_fixture).RunAsync(["activate", entry.Id.ToString()]);

        Assert.Equal(0, result.ExitCode);
        Assert.False(_fixture.Launcher.Exists(entry));
    }

    [Fact]
    public async Task Deactivate_KeepsTheLauncherEntry()
    {
        if (OperatingSystem.IsWindows()) return;
        var entry = await InstallAsync("4.5.1", "--activate");

        var result = await CliTestHarness.Create(_fixture).RunAsync(["deactivate"]);

        Assert.Equal(0, result.ExitCode);
        Assert.True(_fixture.Launcher.Exists(entry));
    }

    [Fact]
    public async Task InstallWithActivate_OverAnActiveInstall_CleansUpThePreviousShim()
    {
        if (OperatingSystem.IsWindows()) return; // Unix shim content is what we can inspect here
        var first = await InstallAsync("4.5.1", "--activate");
        var second = await InstallAsync("4.6.0", "--activate");

        var shim = File.ReadAllText(Path.Combine(_fixture.Paths.GetShimDirectory(InstallScope.User), "godot"));
        Assert.Contains(second.Path, shim);
        Assert.DoesNotContain(first.Path, shim);
        var registry = await _fixture.Registry.LoadAsync();
        Assert.Equal(second.Id, registry.ActiveId);
    }
}
```

Before relying on `InstallWithActivate_OverAnActiveInstall_CleansUpThePreviousShim`: on Linux both installs share one `godot` shim path, so this test passes even without the fix (the second write overwrites the first). It only documents the end state. The real regression test for the cleanup is Step 2.

- [ ] **Step 2: Write the failing cleanup test** — in `InstallerServiceIntegrationTests.cs`, a user→global switch leaves a stale shim in the *other* scope's directory; that is what `RemoveActiveAsync` exists to delete:

```csharp
[Fact]
public async Task InstallAsync_WithActivate_RemovesThePreviouslyActiveInstallsShim()
{
    if (OperatingSystem.IsWindows()) return; // Windows global activate needs elevation
    using var fixture = new GodmanTestFixture();
    var installer = new InstallerService(fixture.Paths, fixture.Registry, fixture.Environment);

    var userArchive = MockArchiveFactory.CreateMockGodotArchive();
    var globalArchive = MockArchiveFactory.CreateMockGodotArchive();
    try
    {
        await installer.InstallAsync(new InstallRequest("4.5.1", InstallEdition.Standard, InstallPlatform.Linux,
            InstallScope.User, null, userArchive, null, Activate: true, Force: false));
        var userShim = Path.Combine(fixture.Paths.GetShimDirectory(InstallScope.User), "godot");
        Assert.True(File.Exists(userShim), "arrange: user shim written");

        await installer.InstallAsync(new InstallRequest("4.6.0", InstallEdition.Standard, InstallPlatform.Linux,
            InstallScope.Global, null, globalArchive, null, Activate: true, Force: false));

        Assert.False(File.Exists(userShim), "the previous activation's shim must be cleaned up");
        Assert.True(File.Exists(Path.Combine(fixture.Paths.GetShimDirectory(InstallScope.Global), "godot")));
    }
    finally
    {
        File.Delete(userArchive);
        File.Delete(globalArchive);
    }
}
```

- [ ] **Step 3: Run to verify failure**

Run: `dotnet test --filter "FullyQualifiedName~LauncherLifecycleE2ETests|FullyQualifiedName~RemovesThePreviouslyActiveInstallsShim"`
Expected: build error (`LauncherEntry`, `--no-shortcut` unknown). After adding only the field, the lifecycle and cleanup tests fail on their assertions.

- [ ] **Step 4: Implement**

`InstallEntry` — add after `ChecksumVerified`:

```csharp
/// <summary>
/// Whether this install should have an application-launcher entry. Null means the
/// entry was recorded by godman &lt; 1.4.0, before launcher entries existed; that is
/// treated as "wanted", so activating such an install creates one. False is an
/// explicit <c>install --no-shortcut</c> and is honoured by activate and doctor.
/// </summary>
public bool? LauncherEntry { get; set; }
```

`InstallRequest` — append a last parameter: `KnownChecksum? Known = null, bool CreateLauncherEntry = true);`
`ElevatedInstallPayload` — append: `bool ChecksumVerified = false, bool CreateLauncherEntry = true);`
`BuildElevatedPayload` — pass `request.CreateLauncherEntry` as the last argument.
`ElevatedInstallCommand.BuildRequest` — add the named argument `CreateLauncherEntry: payload.CreateLauncherEntry`.

`InstallerService.InstallAsync` — set the field in the `new InstallEntry { … }` initializer:

```csharp
LauncherEntry = request.CreateLauncherEntry,
```

and replace the block from `registry.Installs.RemoveAll(…)` through the `if (request.Activate) { … }` block with:

```csharp
// Captured before RemoveAll: when --force reinstalls the active install in place,
// the previous active entry is the one being replaced, and its cleanup still has to
// run against the scope it was activated in.
var previousActive = request.Activate ? registry.GetActive() : null;

registry.Installs.RemoveAll(x => string.Equals(x.Path, targetDir, StringComparison.OrdinalIgnoreCase));
registry.Installs.Add(entry);

if (request.CreateLauncherEntry)
{
    _environment.Launcher.Create(entry);
}

if (request.Activate)
{
    // Same cleanup `activate` does. Without it, switching the active install through
    // `install --activate` (and the TUI install dialog, which always activates) left
    // the previous activation's shim, PATH entry and desktop shortcut behind.
    if (previousActive is not null)
    {
        try
        {
            await _environment.RemoveActiveAsync(previousActive, cancellationToken);
        }
        catch (Exception ex)
        {
            _diagnostics?.Warn($"Failed to clean up previous activation ({previousActive.Version}): {ex.Message}");
        }
    }

    registry.MarkActive(entry.Id);
    await _environment.ApplyActiveAsync(entry, cancellationToken);
}
```

`DryRunInstallAsync` — read it; where it prints/returns its planned actions, add a line "Add application launcher entry" when `request.CreateLauncherEntry`. (If it only builds an entry, set `LauncherEntry = request.CreateLauncherEntry` on it.)

`EnvironmentService.ApplyActiveAsync(entry, dryRun, createDesktopShortcut, …)` — in the non-dry-run path, after the platform branch:

```csharp
// Rewritten on every activation, not just created when missing: that is what gives
// pre-1.4.0 installs their entry, and it self-heals one whose target moved.
if (entry.LauncherEntry != false)
{
    _launcher.Create(entry);
}
```

and in `ApplyWindows` remove the `_launcher.Create(entry);` line Task 2 added, keeping only the `if (createDesktopShortcut) _launcher.CreateDesktopShortcut(entry);` part.

`EnvironmentService.RemoveWindows` — replace `_launcher.Delete(entry);` with `_launcher.DeleteDesktopShortcut(entry);` and fix its comment: the Start Menu entry belongs to the install now and is deleted by remove/clean, not by deactivation.

`InstallCommand.Settings` — add next to `--activate`:

```csharp
[CommandOption("--no-shortcut")]
[Description("Do not add an application-launcher entry (Start Menu on Windows, app menu on Linux).")]
public bool NoShortcut { get; set; }
```

pass `CreateLauncherEntry: !settings.NoShortcut` as a named argument to the `new InstallRequest(…)` call, and in `PreviewInstallAsync` renumber the action list so it prints, after "Register in installs.json", `"Add application launcher entry"` when `request.CreateLauncherEntry`, then the activation lines. Use a running `step` counter like `RemoveCommand.PreviewRemove` does.

- [ ] **Step 5: Run** `dotnet test --filter "FullyQualifiedName~LauncherLifecycleE2ETests|FullyQualifiedName~RemovesThePreviouslyActiveInstallsShim"` → PASS; then `dotnet test -v minimal` → all pass. The existing Windows-only `ApplyActiveAsync_WithDesktopShortcut_CreatesShortcut` must still pass on Windows; it now resolves the desktop under `<GODMAN_HOME>\Desktop` — update its expected folder to `_fixture.Paths.DesktopDirectory`.

- [ ] **Step 6: Mutation check (observed)** — comment out the `RemoveActiveAsync(previousActive…)` call, run `InstallAsync_WithActivate_RemovesThePreviouslyActiveInstallsShim`, confirm FAIL, restore. Report the result.

- [ ] **Step 7: Commit**

```bash
git add GodotManager GodotManager.Tests
git commit -m "feat(launcher): create entries on install, backfill on activate

install --activate now cleans up the previously active install the way
activate does, instead of leaking its shim, PATH entry and shortcut.

Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```

---

### Task 5: Remove deletes the launcher entry (CLI, elevated, TUI)

**Files:**
- Modify: `GodotManager/Commands/RemoveCommand.cs`
- Modify: `GodotManager/Commands/ElevatedRemoveCommand.cs`
- Modify: `GodotManager/Tui/TuiApp.cs` (remove handler, ~line 585)
- Test: `GodotManager.Tests/E2E/LauncherLifecycleE2ETests.cs`

**Interfaces:**
- Consumes: `EnvironmentService.Launcher.Delete(InstallEntry)`.

- [ ] **Step 1: Write the failing tests** (append to `LauncherLifecycleE2ETests`)

```csharp
[Fact]
public async Task Remove_NonActiveInstall_DeletesItsLauncherEntry()
{
    if (OperatingSystem.IsWindows()) return;
    var entry = await InstallAsync("4.5.1");

    var result = await CliTestHarness.Create(_fixture).RunAsync(["remove", entry.Id.ToString()]);

    Assert.Equal(0, result.ExitCode);
    Assert.False(_fixture.Launcher.Exists(entry));
}

[Fact]
public async Task Remove_ActiveInstall_DeletesItsLauncherEntry()
{
    if (OperatingSystem.IsWindows()) return;
    var entry = await InstallAsync("4.5.1", "--activate");

    var result = await CliTestHarness.Create(_fixture).RunAsync(["remove", entry.Id.ToString()]);

    Assert.Equal(0, result.ExitCode);
    Assert.False(_fixture.Launcher.Exists(entry));
}

[Fact]
public async Task Remove_WhenRegistrySaveFails_KeepsLauncherEntry()
{
    if (OperatingSystem.IsWindows() || Environment.IsPrivilegedProcess) return; // POSIX permission simulation
    var installPath = Path.Combine(_fixture.TempRoot, "global-install");
    Directory.CreateDirectory(installPath);
    var entry = InstallEntryFactory.Create(scope: InstallScope.Global, path: installPath);
    entry.LauncherEntry = true;
    await _fixture.Registry.SaveAsync(new InstallRegistry { Installs = [entry] });
    _fixture.Launcher.Create(entry);
    File.SetUnixFileMode(_fixture.Paths.GlobalRegistryFile, UnixFileMode.UserRead);

    try
    {
        var result = await CliTestHarness.Create(_fixture).RunAsync(["remove", entry.Id.ToString()]);

        Assert.NotEqual(0, result.ExitCode);
        Assert.True(_fixture.Launcher.Exists(entry));
    }
    finally
    {
        File.SetUnixFileMode(_fixture.Paths.GlobalRegistryFile, UnixFileMode.UserRead | UnixFileMode.UserWrite);
    }
}
```

- [ ] **Step 2: Run** `dotnet test --filter "FullyQualifiedName~LauncherLifecycleE2ETests"` → the two `Remove_*_DeletesItsLauncherEntry` tests FAIL; `KeepsLauncherEntry` passes (nothing deletes yet — it pins the ordering for Step 3).

- [ ] **Step 3: Implement** — in each of the three removal paths, call the launcher delete **after** `SaveAsync` succeeds:

`RemoveCommand.ExecuteAsync`, directly after `await _registry.SaveAsync(registry);`:

```csharp
// After the save, not before: if the registry write fails (a global entry without
// privileges), the entry stays registered and its launcher entry must stay with it.
_environment.Launcher.Delete(install);
```

`ElevatedRemoveCommand.ExecuteAsync`, directly after its `await _registry.SaveAsync(registry);` (inside the try): `_environment.Launcher.Delete(install);` with the same one-line comment.

`TuiApp` remove handler, directly after `await _registry.SaveAsync(registry);` in the non-elevated branch: `_environment.Launcher.Delete(entry);` with the same comment. (The elevated branch delegates to `remove-elevated`, which now does it.)

`RemoveCommand.PreviewRemove` — add a step line `"Remove application launcher entry"` after "Unregister from installs.json" (use the existing `step` counter; start it at 3 and print the new line as step 2).

- [ ] **Step 4: Run** the lifecycle tests (all PASS) and `dotnet test -v minimal`.

- [ ] **Step 5: Mutation check (observed)** — move `_environment.Launcher.Delete(install);` in `RemoveCommand` to *before* `SaveAsync`, run `Remove_WhenRegistrySaveFails_KeepsLauncherEntry`, confirm FAIL, restore.

- [ ] **Step 6: Commit**

```bash
git add GodotManager GodotManager.Tests
git commit -m "fix(remove): delete the install's launcher entry and shortcuts

Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```

---

### Task 6: Clean deletes all launcher entries

**Files:**
- Modify: `GodotManager/Commands/CleanCommand.cs`
- Modify: `GodotManager/Commands/ElevatedCleanCommand.cs`
- Test: `GodotManager.Tests/CleanCommandTests.cs`

**Interfaces:**
- Consumes: `LauncherService.DeleteAll(InstallScope, IEnumerable<InstallEntry>)`.
- Produces: `CleanCommand.CleanupAll(AppPaths paths, IReadOnlyList<InstallEntry>? installs = null)`.

- [ ] **Step 1: Write the failing test** (in `CleanCommandTests`, using its `_fixture`)

```csharp
[Fact]
public void Clean_RemovesOnlyGodmanLauncherEntries()
{
    if (OperatingSystem.IsWindows()) return;
    var installPath = Path.Combine(_fixture.TempRoot, "i");
    Directory.CreateDirectory(installPath);
    var user = InstallEntryFactory.Create(path: installPath);
    var global = InstallEntryFactory.Create(scope: InstallScope.Global, path: installPath);
    _fixture.Launcher.Create(user);
    _fixture.Launcher.Create(global);
    var foreign = Path.Combine(_fixture.Paths.GetLauncherDirectory(InstallScope.User), "org.gnome.Foo.desktop");
    File.WriteAllText(foreign, "[Desktop Entry]\n");

    CleanCommand.CleanupAll(_fixture.Paths, [user, global]);

    Assert.False(_fixture.Launcher.Exists(user));
    Assert.False(_fixture.Launcher.Exists(global));
    Assert.False(File.Exists(_fixture.Paths.GetLauncherIconPath(InstallScope.User)));
    Assert.True(File.Exists(foreign));
}
```

- [ ] **Step 2: Run** `dotnet test --filter "FullyQualifiedName~Clean_RemovesOnlyGodmanLauncherEntries"` → build error (no 2-arg `CleanupAll`).

- [ ] **Step 3: Implement**

`CleanCommand`: inject the registry — constructor `CleanCommand(AppPaths paths, RegistryService registry)`, field `_registry`. In `Execute`, before the elevation branch, load the installs (the registry files are about to be deleted):

```csharp
// Read before anything is deleted: the registry is the only record of which
// desktop shortcuts on Windows are ours.
var installs = _registry.LoadAsync(cancellationToken).GetAwaiter().GetResult().Installs;
```

pass them to `CleanupAll(_paths, installs)`. Change `CleanupAll`:

```csharp
internal static void CleanupAll(AppPaths paths, IReadOnlyList<InstallEntry>? installs = null)
{
    var launcher = new LauncherService(paths);
    foreach (var scope in new[] { InstallScope.User, InstallScope.Global })
    {
        foreach (var removed in launcher.DeleteAll(scope, installs ?? []))
        {
            AnsiConsole.MarkupLineInterpolated($"[green]Removed[/] launcher entry: {removed}");
        }
    }

    CleanupDirectory(paths.ConfigDirectory, "config");
    // ... rest unchanged ...
}
```

Extend `HasGlobalCleanupTargets` with `|| Directory.Exists(paths.GetLauncherDirectory(InstallScope.Global))` so a Windows machine whose only global leftover is the Start Menu folder still elevates.

`ElevatedCleanCommand`: inject `RegistryService` too, load installs the same way, call `CleanCommand.CleanupAll(_paths, installs)`.

Update the confirm prompt text to "This will remove godman installs, shims, launcher entries, and config. Continue?".

- [ ] **Step 4: Run** the test (PASS) and `dotnet test -v minimal`.

- [ ] **Step 5: Commit**

```bash
git add GodotManager/Commands GodotManager.Tests
git commit -m "fix(clean): remove godman's launcher entries and icon

Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```

---

### Task 7: Doctor check + TUI install checkbox

**Files:**
- Modify: `GodotManager/Commands/DoctorCommand.cs`
- Modify: `GodotManager/Tui/Views/InstallDialog.cs`
- Test: `GodotManager.Tests/DoctorCommandTests.cs`

**Interfaces:**
- Consumes: `LauncherService.Exists`, `InstallEntry.LauncherEntry`, `InstallRequest.CreateLauncherEntry`.

- [ ] **Step 1: Write the failing tests** — add a helper to `DoctorCommandTests` (doctor writes through the static `AnsiConsole`, so the tester's console must be swapped in, exactly as the file's existing tests do):

```csharp
private static async Task<string> RunDoctorAsync(GodmanTestFixture fixture)
{
    var app = CliTestHarness.Create(fixture);
    var originalConsole = AnsiConsole.Console;
    AnsiConsole.Console = app.Console;
    try
    {
        var result = await app.RunAsync(["doctor"]);
        Assert.Equal(0, result.ExitCode);
        return result.Output;
    }
    finally
    {
        AnsiConsole.Console = originalConsole;
    }
}

[Fact]
public async Task Doctor_ReportsInstallMissingItsLauncherEntry()
{
    using var fixture = new GodmanTestFixture();
    var path = Path.Combine(fixture.TempRoot, "i");
    Directory.CreateDirectory(path);
    var entry = InstallEntryFactory.Create(version: "4.5.1", path: path);   // LauncherEntry null: legacy, wanted
    await fixture.Registry.SaveAsync(new InstallRegistry { Installs = [entry] });

    var output = await RunDoctorAsync(fixture);

    Assert.Contains("Launcher entry missing", output);
    Assert.Contains(entry.Id.ToString(), output);
}

[Fact]
public async Task Doctor_DoesNotReportAnOptedOutInstall()
{
    using var fixture = new GodmanTestFixture();
    var path = Path.Combine(fixture.TempRoot, "i");
    Directory.CreateDirectory(path);
    var entry = InstallEntryFactory.Create(version: "4.5.1", path: path);
    entry.LauncherEntry = false;
    await fixture.Registry.SaveAsync(new InstallRegistry { Installs = [entry] });

    var output = await RunDoctorAsync(fixture);

    Assert.DoesNotContain("Launcher entry missing", output);
}
```

If doctor's `Assert.Equal(0, result.ExitCode)` does not hold on this environment for an otherwise-healthy registry, check what the existing doctor tests assert about the exit code and match it.

- [ ] **Step 2: Run** → the first test FAILS.

- [ ] **Step 3: Implement doctor** — after the "Active install directory missing" block:

```csharp
// One entry per install is created on install; pre-1.4.0 installs never had one,
// and a user may have deleted it. An explicit --no-shortcut is not a problem.
var launcher = new LauncherService(_paths, _diagnostics);
foreach (var install in registry.Installs.Where(x => x.LauncherEntry != false && !launcher.Exists(x)))
{
    AnsiConsole.MarkupLineInterpolated($"[yellow]Launcher entry missing[/] for {install.Version} ({install.Edition}, {install.Scope}) [grey]{install.Id}[/]");
    AnsiConsole.MarkupLineInterpolated($"[grey]  Run: godman activate {install.Id}[/]");
}
```

- [ ] **Step 4: Implement the TUI checkbox** — `InstallDialog`: add a field `private readonly CheckBox _launcherCheckBox;`, construct it after the scope selector:

```csharp
_launcherCheckBox = new CheckBox
{
    X = 14, Y = 7,
    Text = "Add to application launcher",
    Value = CheckState.Checked
};
```

move `_progressBar` to `Y = 9` and `_statusLabel` to `Y = 10`, add `_launcherCheckBox` to the `Add(…)` call, and pass `CreateLauncherEntry: _launcherCheckBox.Value == CheckState.Checked` as a named argument to the `new InstallRequest(…)` in `DoInstallAsync`. (`CheckBox.Value` / `CheckState` are the Terminal.Gui 2.4.17 names — verified against the package's XML docs.) Terminal.Gui cannot be instantiated under xunit here; this is covered by the manual checklist.

- [ ] **Step 5: Run** `dotnet build` (0 warnings introduced) and `dotnet test -v minimal`.

- [ ] **Step 6: Commit**

```bash
git add GodotManager GodotManager.Tests
git commit -m "feat(launcher): doctor reports missing entries; TUI opt-out checkbox

Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```

---

### Task 8: Elevated-install cancellation follow-ups (2.2, 2.3, 2.4)

**Files:**
- Modify: `GodotManager/Services/InstallerService.cs` (`TryKillProcessTreeAsync`, `RunElevatedInstallAsync`, `InstallWithElevationAsync`)
- Modify: `GodotManager.Tests/InstallerServiceInternalsTests.cs`

**Interfaces:**
- Produces: `internal static Task<bool> TryKillProcessTreeAsync(Process process, TimeSpan? timeout = null, Func<Process, CancellationToken, Task>? waitForExit = null)` — true when the process is confirmed gone.

- [ ] **Step 1: Write the failing tests**

```csharp
[Fact]
public async Task TryKillProcessTreeAsync_WhenExitIsNeverObserved_GivesUpAfterTheTimeoutAndReturnsFalse()
{
    using var process = StartLongRunningProcess();
    try
    {
        var stopwatch = Stopwatch.StartNew();
        var confirmed = await InstallerService.TryKillProcessTreeAsync(
            process,
            TimeSpan.FromMilliseconds(200),
            waitForExit: (_, token) => Task.Delay(Timeout.Infinite, token));

        Assert.False(confirmed);
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(10), $"took {stopwatch.Elapsed}");
    }
    finally
    {
        try { process.Kill(entireProcessTree: true); } catch { }
    }
}

[Fact]
public async Task TryKillProcessTreeAsync_WithARunningProcess_ReturnsTrue()
{
    using var process = StartLongRunningProcess();

    Assert.True(await InstallerService.TryKillProcessTreeAsync(process));
    Assert.True(process.HasExited);
}
```

(add `using System.Threading;`).

- [ ] **Step 2: Run** `dotnet test --filter "FullyQualifiedName~InstallerServiceInternalsTests"` → build error (no overload / not `Task<bool>`).

- [ ] **Step 3: Implement 2.2** — replace `TryKillProcessTreeAsync`:

```csharp
internal static async Task<bool> TryKillProcessTreeAsync(
    Process process,
    TimeSpan? timeout = null,
    Func<Process, CancellationToken, Task>? waitForExit = null)
{
    waitForExit ??= static (p, token) => p.WaitForExitAsync(token);

    try
    {
        if (!process.HasExited)
        {
            process.Kill(entireProcessTree: true);
        }

        // Bounded: Kill() only requests termination, and an elevated child the OS
        // will not let us reap must not hang cancellation forever.
        using var cts = new CancellationTokenSource(timeout ?? TimeSpan.FromSeconds(10));
        await waitForExit(process, cts.Token);
        return true;
    }
    catch (OperationCanceledException)
    {
        return false;
    }
    catch (InvalidOperationException)
    {
        // No process is associated with this object any more (it was disposed or never
        // started). Nothing is left to wait for.
        return true;
    }
    catch (Win32Exception)
    {
        // The OS refused the kill (e.g. already terminating, or access denied).
        return false;
    }
}
```

Update the XML `<summary>` to say the wait is bounded and what the bool means. In `RunElevatedInstallAsync`'s `catch (OperationCanceledException)`, replace `await TryKillProcessTreeAsync(process);` with:

```csharp
if (!await TryKillProcessTreeAsync(process))
{
    // The cache entry is kept on cancellation regardless (see InstallWithElevationAsync),
    // so an unconfirmed kill costs nothing but this warning.
    _diagnostics?.Warn("the elevated installer did not confirm exit after cancellation; it may still be running.");
}
```

- [ ] **Step 4: Implement 2.3** — in `InstallWithElevationAsync`:

```csharp
var completed = false;
try
{
    await RunElevatedInstallAsync(elevatedRequest, cancellationToken);
    completed = true;
}
finally
{
    // (existing comment, plus:) A token cancelled *after* the child already
    // succeeded does not make this a cancelled install -- keying on `completed`
    // rather than the token alone is what stops that late cancel leaking the entry.
    if (plan.CacheFilePath is not null && (completed || !cancellationToken.IsCancellationRequested))
    {
        _download.DeleteCacheEntry(plan.CacheFilePath);
    }
}
```

This path is Windows-only and not unit-reachable on Linux; say so in the task report rather than writing a test that cannot fail.

- [ ] **Step 5: Implement 2.4** — in `InstallerServiceInternalsTests`, in the `<remarks>` of `TryKillProcessTreeAsync_WithAnAlreadyExitedProcess_DoesNotThrow`, replace the sentence beginning "They are kept in production code regardless: .NET's own documentation states Kill() throws InvalidOperationException for an already-exited process on Windows, which this Linux environment cannot exercise either way." with:

"They are kept in production code regardless, but not for the reason once given here: throwing on an already-exited process was .NET Framework behaviour, and this project targets net10.0 only. What the catches actually cover is `InvalidOperationException` when no process is associated with the object any more, and `Win32Exception` when the OS refuses the kill."

- [ ] **Step 6: Run** `dotnet test --filter "FullyQualifiedName~InstallerServiceInternalsTests"` → PASS; then the full suite.

- [ ] **Step 7: Mutation check (observed)** — replace `cts.Token` with `CancellationToken.None` in `TryKillProcessTreeAsync`, run the timeout test with `--blame-hang-timeout 30s`, confirm it hangs/fails, restore. Report.

- [ ] **Step 8: Commit**

```bash
git add GodotManager/Services/InstallerService.cs GodotManager.Tests/InstallerServiceInternalsTests.cs
git commit -m "fix(install): bound the post-kill wait; keep cache only on real cancels

Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```

---

### Task 9: `godman version`

**Files:**
- Create: `GodotManager/Commands/VersionCommand.cs`
- Modify: `GodotManager/Program.cs`, `GodotManager.Tests/Helpers/CliTestHarness.cs`
- Test: `GodotManager.Tests/VersionCommandTests.cs`

- [ ] **Step 1: Write the failing test**

```csharp
using GodotManager.Tests.Helpers;
using Spectre.Console;
using System;
using System.Threading.Tasks;
using Xunit;

namespace GodotManager.Tests;

public class VersionCommandTests : IDisposable
{
    private readonly GodmanTestFixture _fixture = new();
    public void Dispose() => _fixture.Dispose();

    [Fact]
    public async Task Version_PrintsGodmanRuntimeAndOs()
    {
        var app = CliTestHarness.Create(_fixture);
        var originalConsole = AnsiConsole.Console;
        AnsiConsole.Console = app.Console;
        try
        {
            var result = await app.RunAsync(["version"]);

            Assert.Equal(0, result.ExitCode);
            Assert.Contains("godman", result.Output);
            Assert.Contains(System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription, result.Output);
        }
        finally
        {
            AnsiConsole.Console = originalConsole;
        }
    }
}
```

- [ ] **Step 2: Run** → FAILS (unknown command `version`).

- [ ] **Step 3: Implement** `GodotManager/Commands/VersionCommand.cs`:

```csharp
using Spectre.Console;
using Spectre.Console.Cli;
using System.Reflection;
using System.Runtime.InteropServices;

namespace GodotManager.Commands;

internal sealed class VersionCommand : Command<VersionCommand.Settings>
{
    internal sealed class Settings : CommandSettings { }

    /// <summary>Same source Program.cs gives SetApplicationVersion, so the two never disagree.</summary>
    internal static string GodmanVersion =>
        Assembly.GetEntryAssembly()?.GetName().Version?.ToString(3)
        ?? typeof(VersionCommand).Assembly.GetName().Version?.ToString(3)
        ?? "unknown";

    protected override int Execute(CommandContext context, Settings settings, CancellationToken cancellationToken)
    {
        AnsiConsole.MarkupLineInterpolated($"[green]godman[/] {GodmanVersion}");
        AnsiConsole.MarkupLineInterpolated($"[grey]runtime[/] {RuntimeInformation.FrameworkDescription}");
        AnsiConsole.MarkupLineInterpolated($"[grey]os[/] {RuntimeInformation.OSDescription} ({RuntimeInformation.OSArchitecture})");
        return 0;
    }
}
```

In `Program.cs` register `config.AddCommand<VersionCommand>("version").WithDescription("Show godman, runtime and OS versions");` after `doctor`, and change `SetApplicationVersion(...)` to `SetApplicationVersion(VersionCommand.GodmanVersion)`. Register `config.AddCommand<VersionCommand>("version");` in `CliTestHarness`. Check the `Execute` override signature matches the other sync command (`CleanCommand.Execute(CommandContext, Settings, CancellationToken)`).

- [ ] **Step 4: Run** the test (PASS) and the suite.

- [ ] **Step 5: Commit**

```bash
git add GodotManager GodotManager.Tests
git commit -m "feat: add godman version command

Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```

---

### Task 10: Docs and final checks

**Files:**
- Modify: `README.md` (Commands, Paths, attribution), `GodotManager/docs/PLAN.md` (Phase 8 addendum), `CLAUDE.md` (one line on `LauncherService` ownership)

- [ ] **Step 1: README**
  - Commands: `install` gains `--no-shortcut`; add `version`; mention `activate --create-desktop-shortcut` is Windows-only.
  - Paths → Linux: add `~/.local/share/applications/godman-godot-*.desktop` (user) and `/usr/local/share/applications/godman-godot-*.desktop` (global), plus the icon under `…/icons/hicolor/scalable/apps/godman-godot.svg`.
  - Paths → Windows: Start Menu `Programs\godman\Godot <ver> (<edition>).lnk` per install.
  - Notes: "The Godot logo used for Linux launcher entries is by Andrea Calabró, licensed CC BY 4.0."
- [ ] **Step 2: PLAN.md** — under Phase 8, add "Launcher entries per install + 1.3.0 follow-ups" with bullets for each task and the two dropped follow-ups (and why).
- [ ] **Step 3: CLAUDE.md** — under Conventions add: "Launcher entries (`.desktop` / Start Menu `.lnk`) are owned by `LauncherService`, reached through `EnvironmentService.Launcher` so CLI, elevated mirrors and TUI share one path. Remove deletes them only after the registry save succeeds."
- [ ] **Step 4: Verify** — `dotnet build` (no new warnings), `dotnet test -v minimal` (all pass; report counts).
- [ ] **Step 5: Commit**

```bash
git add README.md GodotManager/docs/PLAN.md CLAUDE.md
git commit -m "docs: launcher entries, --no-shortcut, version command

Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```

- [ ] **Step 6: Whole-branch review** — dispatch the `elevation-parity-reviewer` agent on `git diff main...HEAD` restricted to this plan's commits, telling it: `CreateShortcuts`/`DeleteShortcuts` moved from `EnvironmentService` into `LauncherService` (moved, not new); the global `share/applications` write is new machine-wide state; `install --activate` gained a cleanup call; remove paths gained a post-save delete.

## Manual verification (Jan, before tagging)

- Fedora/GNOME user scope: `godman install 4.x` → "Godot 4.x (Standard)" in the overview with the Godot icon; launches; the running window groups under it; `godman remove <id>` → gone.
- Fedora global: `sudo godman install … --scope Global` → file under `/usr/local/share/applications`; `sudo godman remove <id>` → gone.
- Existing 4.7.2: `godman activate <id>` → entry appears; `godman doctor` stops reporting it.
- `godman install … --no-shortcut` → no entry; `activate` does not add one; doctor silent.
- TUI: install with checkbox off and on; TUI remove.
- Windows, both scopes: install → Start Menu entry; activate with `--create-desktop-shortcut`; switch active → previous desktop shortcut gone, Start Menu entries kept; remove → both gone; clean → `Programs\godman` gone.
- `godman version`.
