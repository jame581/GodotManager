using GodotManager.Domain;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;

namespace GodotManager.Tui.Views;

internal sealed class DetailsView : View
{
    private readonly Label _content;

    public DetailsView()
    {
        Width = Dim.Fill();
        Height = Dim.Fill();

        _content = new Label
        {
            Width = Dim.Fill(),
            Height = Dim.Fill(),
            Text = "No install selected."
        };

        Add(_content);
    }

    public void ShowEntry(InstallEntry? entry)
    {
        if (entry is null)
        {
            _content.Text = "No install selected.";
            return;
        }

        var edition = entry.Edition == InstallEdition.DotNet ? ".NET" : "Standard";
        var active = entry.IsActive ? "Yes ✓" : "No";

        _content.Text =
            $"Version:   {entry.Version}\n" +
            $"Edition:   {edition}\n" +
            $"Platform:  {entry.Platform}\n" +
            $"Scope:     {entry.Scope}\n" +
            $"Path:      {entry.Path}\n" +
            $"Added:     {entry.AddedAt:yyyy-MM-dd HH:mm}\n" +
            $"Active:    {active}\n" +
            $"\n" +
            $"[a] Activate  [d] Deactivate  [r] Remove  [?] Help";
    }
}
