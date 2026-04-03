using GodotManager.Config;
using GodotManager.Domain;
using GodotManager.Services;
using Terminal.Gui.App;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;

namespace GodotManager.Tui.Views;

internal sealed class DoctorDialog : Dialog
{
    private readonly RegistryService _registry;
    private readonly EnvironmentService _environment;
    private readonly AppPaths _paths;
    private readonly TextView _reportView;

    public DoctorDialog(
        RegistryService registry,
        EnvironmentService environment,
        AppPaths paths)
    {
        _registry = registry;
        _environment = environment;
        _paths = paths;

        Title = "Doctor";
        Width = Dim.Percent(70);
        Height = Dim.Percent(70);

        _reportView = new TextView
        {
            X = 1, Y = 1,
            Width = Dim.Fill() - 2,
            Height = Dim.Fill() - 4,
            ReadOnly = true,
            Text = "Running checks..."
        };

        var closeButton = new Button { Text = "Close" };
        closeButton.Accepting += (_, _) => RequestStop();

        Add(_reportView);
        AddButton(closeButton);

        _ = RunChecksAsync();
    }

    private async Task RunChecksAsync()
    {
        var report = new System.Text.StringBuilder();
        var passed = 0;
        var failed = 0;

        void Check(string name, bool ok, string detail = "")
        {
            var icon = ok ? "✓" : "✗";
            report.AppendLine($"  {icon} {name}");
            if (!string.IsNullOrEmpty(detail))
                report.AppendLine($"    {detail}");
            if (ok) passed++; else failed++;
        }

        report.AppendLine("=== Godot Manager Doctor ===\n");

        // Check registry
        try
        {
            var registry = await _registry.LoadAsync();
            Check("Registry loads", true, $"{registry.Installs.Count} install(s) registered");

            // Check active install
            var active = registry.GetActive();
            if (active is not null)
            {
                Check("Active install set", true, $"{active.Version} ({active.Edition})");
                Check("Active install path exists", Directory.Exists(active.Path), active.Path);
            }
            else
            {
                Check("Active install set", false, "No active install");
            }

            // Check each install path
            foreach (var entry in registry.Installs)
            {
                Check($"Install path: {entry.Version}", Directory.Exists(entry.Path), entry.Path);
            }
        }
        catch (Exception ex)
        {
            Check("Registry loads", false, ex.Message);
        }

        // Check paths
        Check("Config directory exists", Directory.Exists(_paths.ConfigDirectory), _paths.ConfigDirectory);
        var installRoot = _paths.GetInstallRoot(InstallScope.User);
        Check("Installs directory exists", Directory.Exists(installRoot), installRoot);

        // Check shim
        var shimName = OperatingSystem.IsWindows() ? "godot.cmd" : "godot";
        var shimDir = _paths.GetShimDirectory(InstallScope.User);
        var shimPath = System.IO.Path.Combine(shimDir, shimName);
        Check("Shim exists", File.Exists(shimPath), shimPath);

        // Summary
        report.AppendLine($"\n=== Summary: {passed} passed, {failed} failed ===");

        Application.Instance.Invoke(() =>
        {
            _reportView.Text = report.ToString();
        });
    }
}
