using System.Text.RegularExpressions;

namespace Bundler.Core.Templates;

public static class TemplateRenderer
{
    private static readonly Regex TokenPattern = new(
        "\\{\\{([a-z0-9_]+)\\}\\}",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    public static string Render(string template, IReadOnlyDictionary<string, string> values)
    {
        if (template is null)
        {
            throw new ArgumentNullException(nameof(template));
        }

        if (values is null)
        {
            throw new ArgumentNullException(nameof(values));
        }

        var rendered = TokenPattern.Replace(template, match =>
        {
            var name = match.Groups[1].Value;
            return values.TryGetValue(name, out var value)
                ? value
                : throw new InvalidDataException($"Template variable '{name}' was not provided.");
        });

        return rendered;
    }
}
