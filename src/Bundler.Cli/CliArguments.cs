namespace DotNet.Bundler.Cli;

/// <summary>Parsed command-line state. Exit codes: 0 ok, 1 backend/IO failure, 2 usage or validation failure.</summary>
internal sealed class CliArguments
{
    public string Command { get; private init; } = "";
    public bool Json { get; private init; }
    public bool Quiet { get; private init; }
    public bool Verbose { get; private init; }
    public bool Help { get; private init; }
    public bool ShowVersion { get; private init; }
    public IReadOnlyDictionary<string, string> Options { get; private init; } =
        new Dictionary<string, string>();

    /// <summary>Repeatable --input-dir values, in command-line order.</summary>
    public IReadOnlyList<string> InputDirectories { get; private init; } = [];

    private static readonly HashSet<string> ValueOptions = new(StringComparer.Ordinal)
    {
        "input-dir", "output-dir", "rid", "formats", "product-name", "identifier",
        "package-version", "main-executable", "publisher", "description", "homepage",
        "copyright", "license-file", "icons", "config", "key-file"
    };

    private static bool IsKnownValueOption(string name) =>
        ValueOptions.Contains(name) ||
        // --<format>.<knob> overrides (e.g. --deb.vendor=…) merge into the
        // matching bundler.json section; validated against real sections there.
        (name.Contains('.') && FormatSections.Contains(
            name[..name.IndexOf('.')].ToLowerInvariant()));

    private static readonly HashSet<string> FormatSections = new(StringComparer.Ordinal)
    {
        "nsis", "msi", "app", "dmg", "pkg", "deb", "rpm", "appimage", "archive",
        "alpineapk", "update"
    };

    private static readonly HashSet<string> FlagOptions = new(StringComparer.Ordinal)
    {
        "json", "quiet", "verbose", "help"
    };

    public static CliArguments Parse(string[] args)
    {
        var command = "";
        var options = new Dictionary<string, string>(StringComparer.Ordinal);
        var inputDirs = new List<string>();
        var json = false;
        var quiet = false;
        var verbose = false;
        var help = false;
        var version = false;

        var index = 0;
        if (index < args.Length && !args[index].StartsWith("-", StringComparison.Ordinal))
        {
            command = args[index++];
        }

        for (; index < args.Length; index++)
        {
            var argument = args[index];
            if (!argument.StartsWith("--", StringComparison.Ordinal) || argument.Length == 2)
            {
                throw CliUsageException($"Unexpected argument '{argument}'.");
            }

            var body = argument[2..];
            var equals = body.IndexOf('=');
            var name = equals >= 0 ? body[..equals] : body;
            var inlineValue = equals >= 0 ? body[(equals + 1)..] : null;

            if (name is "version")
            {
                version = true;
                continue;
            }
            if (name is "h")
            {
                help = true;
                continue;
            }

            if (FlagOptions.Contains(name))
            {
                if (inlineValue is not null)
                {
                    throw CliUsageException($"Option '--{name}' does not take a value.");
                }
                switch (name)
                {
                    case "json": json = true; break;
                    case "quiet": quiet = true; break;
                    case "verbose": verbose = true; break;
                    case "help": help = true; break;
                }
                continue;
            }

            if (!IsKnownValueOption(name))
            {
                throw CliUsageException($"Unknown option '--{name}'.");
            }
            // --key-file 只服务 update-keygen；放进其他命令会在加载层报成误导性的
            // Unknown configuration key。
            if (name is "key-file" && command is not "update-keygen")
            {
                throw CliUsageException($"Option '--key-file' is only valid with 'update-keygen'.");
            }
            var value = inlineValue;
            if (value is null)
            {
                if (index + 1 >= args.Length)
                {
                    throw CliUsageException($"Option '--{name}' requires a value.");
                }
                value = args[++index];
            }
            if (name == "input-dir")
            {
                inputDirs.Add(value);
                // First occurrence feeds the config path like before; extras merge in CliProgram.
                if (inputDirs.Count == 1)
                {
                    options.TryAdd(name, value);
                }
                continue;
            }
            if (!options.TryAdd(name, value))
            {
                throw CliUsageException($"Option '--{name}' was specified more than once.");
            }
        }

        if (quiet && verbose)
        {
            throw CliUsageException("--quiet and --verbose cannot be combined.");
        }

        if (Environment.GetEnvironmentVariable("DOTNET_BUNDLER_VERBOSE") is { Length: > 0 } env &&
            env is not "0" and not "false" && !quiet)
        {
            verbose = true;
        }

        return new CliArguments
        {
            Command = command,
            Json = json,
            Quiet = quiet,
            Verbose = verbose,
            Help = help,
            ShowVersion = version,
            Options = options,
            InputDirectories = inputDirs
        };
    }

    public string RequiredOption(string name) =>
        Options.TryGetValue(name, out var value) && !string.IsNullOrWhiteSpace(value)
            ? value
            : throw CliUsageException($"Option '--{name}' is required.");

    public string? OptionalOption(string name) =>
        Options.TryGetValue(name, out var value) && !string.IsNullOrWhiteSpace(value)
            ? value
            : null;

    private static CliUsageException CliUsageException(string message) => new(message);
}

/// <summary>Usage/argument error → exit code 2.</summary>
internal sealed class CliUsageException : Exception
{
    public CliUsageException(string message) : base(message)
    {
    }
}
