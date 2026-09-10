using System.Text.RegularExpressions;

namespace Bundler.Core.Templates;

public static partial class TemplateRenderer
{
    public static string Render(string template, IReadOnlyDictionary<string, string> values)
    {
        ArgumentNullException.ThrowIfNull(template);
        ArgumentNullException.ThrowIfNull(values);

        var rendered = TokenPattern().Replace(template, match =>
        {
            var name = match.Groups[1].Value;
            return values.TryGetValue(name, out var value)
                ? value
                : throw new InvalidDataException($"Template variable '{name}' was not provided.");
        });

        return rendered;
    }

    [GeneratedRegex("\\{\\{([a-z0-9_]+)\\}\\}", RegexOptions.CultureInvariant)]
    private static partial Regex TokenPattern();
}
