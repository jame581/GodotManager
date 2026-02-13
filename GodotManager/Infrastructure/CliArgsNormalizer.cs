namespace GodotManager.Infrastructure;

internal static class CliArgsNormalizer
{
    public static string[] Normalize(string[] input)
    {
        if (input.Length == 0)
        {
            return input;
        }

        var first = input[0];
        if (string.Equals(first, "-v", StringComparison.OrdinalIgnoreCase)
            || string.Equals(first, "--version", StringComparison.OrdinalIgnoreCase)
            || string.Equals(first, "/v", StringComparison.OrdinalIgnoreCase))
        {
            return ["version", .. input.Skip(1)];
        }

        if (first.StartsWith("--", StringComparison.Ordinal) && first.Length > 2)
        {
            var command = first.TrimStart('-');
            return [command, .. input.Skip(1)];
        }

        return input;
    }
}