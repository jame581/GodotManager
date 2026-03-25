using Spectre.Console.Cli;

namespace GodotManager.Infrastructure;

internal sealed class VerboseInterceptor : ICommandInterceptor
{
    private readonly DiagnosticContext _diagnostics;

    public VerboseInterceptor(DiagnosticContext diagnostics)
    {
        _diagnostics = diagnostics;
    }

    public void Intercept(CommandContext context, CommandSettings settings)
    {
        if (settings is GlobalSettings global)
        {
            _diagnostics.Verbose = global.Verbose;
        }
    }
}
