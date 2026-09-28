using System.Text.RegularExpressions;

namespace MateMCP.Agent.Companion.Services;

internal static class TerminalOutputSanitizer
{
    private static readonly Regex EscapeSequence = new(
        @"(?:\x1B\][^\x07\x1B]*(?:\x07|\x1B\\)|\x1B[P^_][\s\S]*?\x1B\\|\x1B\[[0-?]*[ -/]*[@-~]|\x1B[@-_]|\x9B[0-?]*[ -/]*[@-~])",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex NonRenderingControlCharacter = new(
        @"[\x00-\x08\x0B\x0C\x0E-\x1F\x7F-\x9F]",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public static string ToPlainText(string? output)
    {
        if (string.IsNullOrEmpty(output))
            return string.Empty;

        var withoutEscapeSequences = EscapeSequence.Replace(output, string.Empty);
        return NonRenderingControlCharacter.Replace(withoutEscapeSequences, string.Empty);
    }
}