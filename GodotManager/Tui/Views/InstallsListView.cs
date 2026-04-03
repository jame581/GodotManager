using System.Collections.ObjectModel;
using GodotManager.Domain;
using GodotManager.Services;
using Terminal.Gui.Input;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;

namespace GodotManager.Tui.Views;

internal sealed class InstallsListView : View
{
    private readonly ListView _listView;
    private readonly ObservableCollection<string> _items = new();
    private List<InstallEntry> _entries = [];

    public event EventHandler<InstallEntry?>? SelectionChanged;

    public InstallEntry? SelectedEntry =>
        _listView.SelectedItem is { } idx && idx >= 0 && idx < _entries.Count
            ? _entries[idx]
            : null;

    public InstallsListView()
    {
        CanFocus = true;
        Width = Dim.Fill();
        Height = Dim.Fill();

        _listView = new ListView
        {
            Width = Dim.Fill(),
            Height = Dim.Fill(),
            CanFocus = true,
            Source = new ListWrapper<string>(_items)
        };

        _listView.ValueChanged += (_, _) =>
        {
            SelectionChanged?.Invoke(this, SelectedEntry);
        };

        Add(_listView);
    }

    public void SetInstalls(InstallRegistry registry)
    {
        _entries = registry.Installs
            .OrderByDescending(e => e.IsActive)
            .ThenBy(e => e.Version)
            .ToList();

        _items.Clear();
        foreach (var entry in _entries)
        {
            var marker = entry.IsActive ? "▸ " : "  ";
            var edition = entry.Edition == InstallEdition.DotNet ? ".NET" : "Std";
            _items.Add($"{marker}{entry.Version} ({edition})");
        }

        if (_entries.Count > 0)
        {
            _listView.SelectedItem = 0;
            SelectionChanged?.Invoke(this, SelectedEntry);
        }
    }
}
