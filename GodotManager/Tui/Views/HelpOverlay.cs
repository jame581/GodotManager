using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;

namespace GodotManager.Tui.Views;

internal sealed class HelpOverlay : Dialog
{
    public HelpOverlay()
    {
        Title = "Keyboard Shortcuts";
        Width = Dim.Percent(50);
        Height = Dim.Percent(60);

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
                "\n" +
                "=== Actions ===\n" +
                "  a                  Activate selected install\n" +
                "  d                  Deactivate current install\n" +
                "  r                  Remove selected install\n" +
                "\n" +
                "=== Views ===\n" +
                "  F1                 Toggle Browse panel\n" +
                "  F2                 Open Install dialog\n" +
                "  F3                 Open Doctor dialog\n" +
                "  ?                  Show this help\n" +
                "\n" +
                "=== Browse Panel ===\n" +
                "  Enter              Install selected version\n" +
                "  /                  Focus filter field\n" +
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
