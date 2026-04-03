using System.Collections.ObjectModel;
using GodotManager.Services;
using Terminal.Gui.App;
using Terminal.Gui.Input;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;

namespace GodotManager.Tui.Views;

internal sealed class BrowseView : View
{
    private readonly GodotVersionFetcher _fetcher;
    private readonly IApplication _app;
    private readonly TextField _filterField;
    private readonly CheckBox _stableOnly;
    private readonly ListView _listView;
    private readonly ObservableCollection<string> _items = new();
    private List<GodotRelease> _allReleases = [];
    private List<GodotRelease> _filteredReleases = [];

    public event EventHandler<GodotRelease>? VersionSelected;

    public BrowseView(GodotVersionFetcher fetcher, IApplication app)
    {
        _fetcher = fetcher;
        _app = app;

        Width = Dim.Fill();
        Height = Dim.Fill();
        CanFocus = true;
        TabStop = TabBehavior.TabGroup;

        var filterLabel = new Label
        {
            Text = "Filter:",
            X = 0,
            Y = 0
        };

        _filterField = new TextField
        {
            X = Pos.Right(filterLabel) + 1,
            Y = 0,
            Width = Dim.Fill() - 20,
            CanFocus = true
        };
        _filterField.TextChanged += (_, _) => ApplyFilter();

        _stableOnly = new CheckBox
        {
            Text = "Stable only",
            X = Pos.AnchorEnd(15),
            Y = 0,
            Value = CheckState.Checked
        };
        _stableOnly.ValueChanged += (_, _) => ApplyFilter();

        _listView = new ListView
        {
            X = 0,
            Y = 2,
            Width = Dim.Fill(),
            Height = Dim.Fill(),
            CanFocus = true,
            Source = new ListWrapper<string>(_items)
        };

        _listView.Accepting += (_, _) =>
        {
            if (_listView.SelectedItem is { } idx && idx >= 0 && idx < _filteredReleases.Count)
            {
                VersionSelected?.Invoke(this, _filteredReleases[idx]);
            }
        };

        Add(filterLabel, _filterField, _stableOnly, _listView);
    }

    public async Task LoadVersionsAsync()
    {
        try
        {
            _allReleases = await _fetcher.FetchReleasesAsync();
            _app.Invoke(() => ApplyFilter());
        }
        catch (Exception ex)
        {
            _app.Invoke(() =>
            {
                _items.Clear();
                _items.Add($"Error loading versions: {ex.Message}");
            });
        }
    }

    private void ApplyFilter()
    {
        var filterText = _filterField.Text?.Trim() ?? "";
        var stableOnly = _stableOnly.Value == CheckState.Checked;

        _filteredReleases = _allReleases
            .Where(r => !stableOnly || r.IsStable)
            .Where(r => string.IsNullOrEmpty(filterText) ||
                        r.Version.Contains(filterText, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(r => r.PublishedAt)
            .ToList();

        _items.Clear();
        foreach (var release in _filteredReleases)
        {
            var editions = new List<string>();
            if (release.HasStandard) editions.Add("Std");
            if (release.HasDotNet) editions.Add(".NET");
            var stable = release.IsStable ? "" : " [pre]";
            _items.Add($"{release.Version} ({string.Join(", ", editions)}){stable}");
        }
    }
}
