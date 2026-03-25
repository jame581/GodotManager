using Spectre.Console.Cli;
using System.ComponentModel;

namespace GodotManager.Infrastructure;

internal class GlobalSettings : CommandSettings
{
    [CommandOption("--verbose|-V")]
    [Description("Enable diagnostic warnings for best-effort operations.")]
    public bool Verbose { get; set; }
}
