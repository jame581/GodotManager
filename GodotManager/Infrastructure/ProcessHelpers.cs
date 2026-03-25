namespace GodotManager.Infrastructure;

internal static class ProcessHelpers
{
    public static string QuoteArg(string arg)
    {
        if (string.IsNullOrWhiteSpace(arg) || arg.Contains(' ') || arg.Contains('"'))
        {
            return "\"" + arg.Replace("\"", "\\\"") + "\"";
        }

        return arg;
    }
}
