using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;

namespace GodotManager.Tui.Views;

internal sealed class HelpOverlay : Dialog
{
    public HelpOverlay()
    {
        Title = "Keyboard Shortcuts";
        Width = Dim.Percent(85);
        Height = Dim.Percent(75);

        var helpText = new TextView
        {
            X = 1, Y = 1,
            Width = Dim.Fill() - 2,
            Height = Dim.Fill() - 4,
            ReadOnly = true,
            Text =
                "=== Navigation ===\n" +
                "  Tab / Shift+Tab    Switch panels\n" +
                "  ↑ / ↓              Move in list\n" +
                "  Enter              Select / Confirm\n" +
                "  Esc                Close dialog / Exit Browse\n" +
                "\n" +
                "=== Actions ===\n" +
                "  a                  Activate selected install\n" +
                "  d                  Deactivate current install\n" +
                "  r                  Remove selected install\n" +
                "  (In Browse, a/d/r are blocked — press Tab first.)\n" +
                "\n" +
                "=== Views ===\n" +
                "  F1                 Open Browse panel\n" +
                "  F2                 Open Install dialog\n" +
                "  F3                 Open Doctor dialog\n" +
                "  ?                  Show this help\n" +
                "\n" +
                "=== Browse Panel ===\n" +
                "  Enter              Install selected version\n" +
                "  Tab / Esc          Exit Browse, return to Installs\n" +
                "\n" +
                "=== General ===\n" +
                "  q / Ctrl+Q         Quit"
        };

        var closeButton = new Button { Text = "Close" };
        closeButton.Accepting += (_, _) => RequestStop();

        Add(helpText);
        AddButton(closeButton);
    }
}
