using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization.Metadata;
using DotNet.Bundler;
using DotNet.Bundler.AlpineApk;
using DotNet.Bundler.Archive;
using DotNet.Bundler.Deb;
using DotNet.Bundler.MacApp;
#if BUNDLER_HOST_MACOS
using DotNet.Bundler.MacDmg;
using DotNet.Bundler.MacPkg;
#endif
using DotNet.Bundler.Nsis;
using DotNet.Bundler.Rpm;
#if BUNDLER_HOST_WINDOWS
using DotNet.Bundler.Wix;
#endif
#if BUNDLER_HOST_LINUX
using DotNet.Bundler.AppImage;
#endif

namespace DotNet.Bundler.Cli;

/// <summary>
/// bundler.json document: shared bundle fields + per-format sections whose
/// property names match each *BundleConfiguration type verbatim (camelCase).
/// CLI options override file values; '--&lt;fmt&gt;.&lt;knob&gt;=&lt;value&gt;'
/// merges into the matching section before deserialization.
/// </summary>
internal sealed class CliResolvedConfiguration
{
    public required BundleConfiguration Bundle { get; init; }
    public NsisBundleConfiguration? Nsis { get; init; }
#if BUNDLER_HOST_WINDOWS
    public WixBundleConfiguration? Msi { get; init; }
#endif
    public MacAppBundleConfiguration? App { get; init; }
#if BUNDLER_HOST_MACOS
    public MacDmgBundleConfiguration? Dmg { get; init; }
    public MacPkgBundleConfiguration? Pkg { get; init; }
#endif
    public DebBundleConfiguration? Deb { get; init; }
    public RpmBundleConfiguration? Rpm { get; init; }
    public AlpineApkBundleConfiguration? AlpineApk { get; init; }
#if BUNDLER_HOST_LINUX
    public AppImageBundleConfiguration? AppImage { get; init; }
#endif
    public ArchiveBundleConfiguration? Archive { get; init; }
}

internal static class CliConfig
{
    private static readonly JsonDocumentOptions DocumentOptions = new()
    {
        CommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true
    };

    private static readonly HashSet<string> FormatSections = new(StringComparer.Ordinal)
    {
        "nsis",
#if BUNDLER_HOST_WINDOWS
        "msi",
#endif
        "app",
#if BUNDLER_HOST_MACOS
        "dmg", "pkg",
#endif
        "deb", "rpm",
#if BUNDLER_HOST_LINUX
        "appimage",
#endif
        "archive", "alpineapk"
    };

    public static CliResolvedConfiguration Resolve(CliArguments parsed)
    {
        var configPath = parsed.OptionalOption("config");
        JsonObject document;
        string baseDirectory;
        if (configPath is not null)
        {
            var fullPath = Path.GetFullPath(configPath);
            if (!File.Exists(fullPath))
            {
                throw new CliUsageException($"Configuration file not found: {fullPath}");
            }
            document = JsonNode.Parse(File.ReadAllText(fullPath), documentOptions: DocumentOptions) as JsonObject
                ?? throw new CliUsageException($"Configuration file '{fullPath}' must be a JSON object.");
            baseDirectory = Path.GetDirectoryName(fullPath)!;
        }
        else
        {
            document = new JsonObject();
            baseDirectory = Directory.GetCurrentDirectory();
        }

        ApplyCliOverrides(document, parsed);
        ResolveRelativePaths(document, baseDirectory);
        EnforceSchema(document);
        return Materialize(document, parsed);
    }

    // Schema is fixed: unknown keys (typos) are rejected rather than ignored.
    private static readonly HashSet<string> TopLevelFields = new(StringComparer.OrdinalIgnoreCase)
    {
        "productName", "identifier", "version", "publisher", "description", "homepage",
        "copyright", "licenseFile", "outputDirectory", "icons", "resources",
        "fileAssociations", "urlProtocols", "targets"
    };

    private static readonly HashSet<string> TargetFields_Allowed = new(StringComparer.OrdinalIgnoreCase)
    {
        "runtimeIdentifier", "inputDirectory", "mainExecutable", "signingFiles", "formats"
    };

    private static readonly Dictionary<string, Type> SectionTypes = new(StringComparer.Ordinal)
    {
        ["nsis"] = typeof(NsisBundleConfiguration),
#if BUNDLER_HOST_WINDOWS
        ["msi"] = typeof(WixBundleConfiguration),
#endif
        ["app"] = typeof(MacAppBundleConfiguration),
#if BUNDLER_HOST_MACOS
        ["dmg"] = typeof(MacDmgBundleConfiguration),
        ["pkg"] = typeof(MacPkgBundleConfiguration),
#endif
        ["deb"] = typeof(DebBundleConfiguration),
        ["rpm"] = typeof(RpmBundleConfiguration),
#if BUNDLER_HOST_LINUX
        ["appimage"] = typeof(AppImageBundleConfiguration),
#endif
        ["archive"] = typeof(ArchiveBundleConfiguration),
        ["alpineapk"] = typeof(AlpineApkBundleConfiguration)
    };

    private static void EnforceSchema(JsonObject document)
    {
        foreach (var key in document.Select(pair => pair.Key).ToArray())
        {
            if (!TopLevelFields.Contains(key) && !SectionTypes.ContainsKey(key.ToLowerInvariant()))
            {
                throw new CliUsageException(
                    $"Unknown configuration key '{key}'. Known top-level fields: " +
                    string.Join(", ", TopLevelFields.Order()) +
                    "; format sections: " + string.Join(", ", SectionTypes.Keys.Order()) + ".");
            }
        }

        if (document["targets"] is JsonArray targets)
        {
            for (var i = 0; i < targets.Count; i++)
            {
                if (targets[i] is not JsonObject target)
                {
                    throw new CliUsageException($"targets[{i}] must be an object.");
                }
                foreach (var key in target.Select(pair => pair.Key).ToArray())
                {
                    if (!TargetFields_Allowed.Contains(key))
                    {
                        throw new CliUsageException(
                            $"Unknown target key 'targets[{i}].{key}'. Known: " +
                            string.Join(", ", TargetFields_Allowed.Order()) + ".");
                    }
                }
            }
        }

        foreach (var (section, type) in SectionTypes)
        {
            if (document[section] is not JsonObject child)
            {
                continue;
            }
            var known = (BundlerJsonContext.Default.GetTypeInfo(type)?.Properties
                    ?? throw new InvalidOperationException(
                        $"No JSON metadata for {type.Name}."))
                .Select(p => p.Name)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var key in child.Select(pair => pair.Key).ToArray())
            {
                if (!known.Contains(key))
                {
                    throw new CliUsageException(
                        $"Unknown key '{section}.{key}'. Known {section} knobs: " +
                        string.Join(", ", known.Order()) + ".");
                }
            }
        }
    }

    // CLI value options (except --config) override the document's shared fields;
    // --<fmt>.<knob>=<value> merges into that format's section.
    private static void ApplyCliOverrides(JsonObject document, CliArguments parsed)
    {
        foreach (var (name, value) in parsed.Options)
        {
            if (name == "config" || TargetFields.ContainsKey(name) || name == "formats")
            {
                // target-level options are applied onto targets[] in Materialize
                continue;
            }
            var dot = name.IndexOf('.');
            if (dot > 0)
            {
                var section = name[..dot].ToLowerInvariant();
                var knob = name[(dot + 1)..];
                if (!FormatSections.Contains(section))
                {
                    throw new CliUsageException(
                        $"Unknown format section '--{section}'. Supported: {string.Join(", ", FormatSections.Order())}.");
                }
                var child = document[section] as JsonObject ?? new JsonObject();
                child[KnobName(knob)] = ParseValue(value);
                document[section] = child;
                continue;
            }
            document[FieldName(name)] = ParseValue(value);
        }
    }

    // --main-executable → targets[0].mainExecutable etc.; scalar target-level
    // options write into targets[0] (creating it if absent).
    private static readonly Dictionary<string, string> TargetFields = new(StringComparer.Ordinal)
    {
        ["rid"] = "runtimeIdentifier",
        ["input-dir"] = "inputDirectory",
        ["main-executable"] = "mainExecutable"
    };

    private static string FieldName(string option) => option switch
    {
        "product-name" => "productName",
        "package-version" => "version",
        "output-dir" => "outputDirectory",
        "license-file" => "licenseFile",
        var name when TargetFields.ContainsKey(name) => name,
        var other => other
    };

    private static string KnobName(string knob)
    {
        // kebab-case → camelCase
        var parts = knob.Split('-', StringSplitOptions.RemoveEmptyEntries);
        return parts[0].ToLowerInvariant() +
            string.Concat(parts.Skip(1).Select(p => char.ToUpperInvariant(p[0]) + p[1..]));
    }

    private static JsonNode? ParseValue(string value)
    {
        if (value is "true" or "false")
        {
            return JsonValue.Create(value == "true");
        }
        if (int.TryParse(value, out var number))
        {
            return JsonValue.Create(number);
        }
        if (value.StartsWith('[') || value.StartsWith('{'))
        {
            try
            {
                return JsonNode.Parse(value);
            }
            catch (JsonException)
            {
                // fall through: treat as a plain string
            }
        }
        if (value.Contains(','))
        {
            return new JsonArray(value.Split(',', StringSplitOptions.TrimEntries)
                .Select<string, JsonNode?>(entry => entry).ToArray());
        }
        return JsonValue.Create(value);
    }

    private static void ResolveRelativePaths(JsonObject document, string baseDirectory)
    {
        static string Resolve(string baseDir, string path) =>
            Path.IsPathRooted(path) ? path : Path.GetFullPath(path, baseDir);

        foreach (var name in new[] { "licenseFile", "outputDirectory" })
        {
            if (document[name] is JsonValue scalar &&
                scalar.TryGetValue<string>(out var path) && !string.IsNullOrWhiteSpace(path))
            {
                document[name] = Resolve(baseDirectory, path);
            }
        }

        if (document["icons"] is JsonArray icons)
        {
            for (var i = 0; i < icons.Count; i++)
            {
                if (icons[i] is JsonValue v && v.TryGetValue<string>(out var p) && p.Length > 0)
                {
                    icons[i] = Resolve(baseDirectory, p);
                }
            }
        }

        if (document["resources"] is JsonArray resources)
        {
            foreach (var node in resources.OfType<JsonObject>())
            {
                if (node["source"] is JsonValue v && v.TryGetValue<string>(out var p) && p.Length > 0)
                {
                    node["source"] = Resolve(baseDirectory, p);
                }
            }
        }

        if (document["targets"] is JsonArray targets)
        {
            foreach (var node in targets.OfType<JsonObject>())
            {
                foreach (var name in new[] { "inputDirectory" })
                {
                    if (node[name] is JsonValue v && v.TryGetValue<string>(out var p) && p.Length > 0)
                    {
                        node[name] = Resolve(baseDirectory, p);
                    }
                }
                if (node["signingFiles"] is JsonArray signing)
                {
                    for (var i = 0; i < signing.Count; i++)
                    {
                        if (signing[i] is JsonValue v && v.TryGetValue<string>(out var p) && p.Length > 0)
                        {
                            signing[i] = Resolve(baseDirectory, p);
                        }
                    }
                }
            }
        }

        // Format sections: resolve *File scalar knobs and files[].source entries.
        foreach (var section in FormatSections)
        {
            if (document[section] is not JsonObject child)
            {
                continue;
            }
            foreach (var key in child.Select(pair => pair.Key).ToArray())
            {
                var node = child[key];
                if (node is JsonValue v && v.TryGetValue<string>(out var path) &&
                    path.Length > 0 &&
                    (key.EndsWith("File", StringComparison.OrdinalIgnoreCase) ||
                     key.EndsWith("Path", StringComparison.OrdinalIgnoreCase)))
                {
                    child[key] = Resolve(baseDirectory, path);
                }
            }
            foreach (var listName in new[] { "files", "payloadItems", "contents", "frameworks" })
            {
                if (child[listName] is JsonArray entries)
                {
                    foreach (var entry in entries.OfType<JsonObject>())
                    {
                        if (entry["source"] is JsonValue v &&
                            v.TryGetValue<string>(out var p) && p.Length > 0)
                        {
                            entry["source"] = Resolve(baseDirectory, p);
                        }
                    }
                }
            }
        }
    }

    private static CliResolvedConfiguration Materialize(JsonObject document, CliArguments parsed)
    {
        var targetsNode = document["targets"] as JsonArray ?? new JsonArray();
        // CLI single-target options write into targets[0]; with no file targets
        // they must still yield one target entry.
        JsonObject target;
        if (targetsNode.Count > 0 && targetsNode[0] is JsonObject first)
        {
            target = first;
        }
        else
        {
            target = new JsonObject();
            targetsNode.Insert(targetsNode.Count, target);
        }
        foreach (var (option, field) in TargetFields)
        {
            var value = parsed.Options.TryGetValue(option, out var v) ? v : null;
            if (value is not null)
            {
                // CLI 输入相对当前工作目录解析；此处在 ResolveRelativePaths 之后，
                // target 级 CLI 字段需要自己绝对化（后端在独立工作目录执行）。
                target[field] = field == "inputDirectory" && !Path.IsPathRooted(value)
                    ? Path.GetFullPath(value)
                    : value;
            }
        }
        if (parsed.Options.TryGetValue("formats", out var formats))
        {
            target["formats"] = new JsonArray(
                CliProgram.ParseFormats(formats, TargetRid(target, parsed))
                    .Select(f => (JsonNode?)JsonValue.Create(FormatName(f))).ToArray());
        }
        if (target["formats"] is null)
        {
            throw new CliUsageException(
                "Formats are required: pass --formats or set targets[].formats in bundler.json.");
        }

        var bundle = new BundleConfiguration
        {
            ProductName = Text(document, "productName") ?? "",
            Identifier = Text(document, "identifier") ?? "",
            Version = Text(document, "version") ?? "",
            Publisher = Text(document, "publisher"),
            Description = Text(document, "description"),
            Homepage = Text(document, "homepage"),
            Copyright = Text(document, "copyright"),
            LicenseFile = Text(document, "licenseFile"),
            OutputDirectory = Text(document, "outputDirectory") ?? "bundler-out",
            Icons = StringList(document["icons"]),
            Resources = ListOf<BundleResourceConfiguration>(document["resources"]),
            FileAssociations = ListOf<BundleFileAssociationConfiguration>(document["fileAssociations"]),
            UrlProtocols = ListOf<BundleUrlProtocolConfiguration>(document["urlProtocols"]),
            Targets = targetsNode.OfType<JsonObject>().Select(node => new BundleTargetConfiguration
            {
                RuntimeIdentifier = Text(node, "runtimeIdentifier") ?? "",
                InputDirectory = Text(node, "inputDirectory") ?? "",
                MainExecutable = Text(node, "mainExecutable"),
                SigningFiles = StringList(node["signingFiles"]),
                Formats = FormatList(node["formats"], Text(node, "runtimeIdentifier") ?? "")
            }).ToArray()
        };

        return new CliResolvedConfiguration
        {
            Bundle = bundle,
            Nsis = Section<NsisBundleConfiguration>(document, "nsis"),
#if BUNDLER_HOST_WINDOWS
            Msi = Section<WixBundleConfiguration>(document, "msi"),
#endif
            App = Section<MacAppBundleConfiguration>(document, "app"),
#if BUNDLER_HOST_MACOS
            Dmg = Section<MacDmgBundleConfiguration>(document, "dmg"),
            Pkg = Section<MacPkgBundleConfiguration>(document, "pkg"),
#endif
            Deb = Section<DebBundleConfiguration>(document, "deb"),
            Rpm = Section<RpmBundleConfiguration>(document, "rpm"),
#if BUNDLER_HOST_LINUX
            AppImage = Section<AppImageBundleConfiguration>(document, "appimage"),
#endif
            Archive = Section<ArchiveBundleConfiguration>(document, "archive"),
            AlpineApk = Section<AlpineApkBundleConfiguration>(document, "alpineapk")
        };
    }

    private static string TargetRid(JsonObject target, CliArguments parsed) =>
        Text(target, "runtimeIdentifier")
        ?? (parsed.Options.TryGetValue("rid", out var rid) ? rid : null)
        ?? "linux-x64";

    private static string? Text(JsonObject node, string field) =>
        node[field] is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;

    private static string[] StringList(JsonNode? node) =>
        node is JsonArray arr
            ? arr.OfType<JsonValue>().Select(v => v.TryGetValue<string>(out var s) ? s! : "")
                .Where(s => s.Length > 0).ToArray()
            : [];

    private static IReadOnlyList<T> ListOf<T>(JsonNode? node) where T : class, new() =>
        node is JsonArray arr
            ? arr.Select(entry => entry is JsonObject element
                    ? DeserializeWithDefaults(element, TypeInfoFor<T>(), new T())
                    : (T)entry!.Deserialize(TypeInfoFor<T>())!).ToArray()
            : [];

    // The source-generated deserializer routes every init-only member through an
    // object-initializer factory, so keys absent from the user's JSON are assigned
    // default(T) and the type's C# initializers (`= []`, `Signing = new()`,
    // `NotaryWait = true`) are silently lost. Deserialize a member-wise merge of
    // the serialized defaults and the user's object instead; explicit null still
    // maps to null.
    private static T DeserializeWithDefaults<T>(JsonObject overrides, JsonTypeInfo info, T defaults)
        where T : class =>
        (T)MergeWithDefaults(overrides, info,
            (JsonObject)JsonSerializer.SerializeToNode(defaults, info)!).Deserialize(info)!;

    private static JsonObject MergeWithDefaults(
        JsonObject overrides, JsonTypeInfo typeInfo, JsonObject baseline)
    {
        var merged = (JsonObject)baseline.DeepClone();
        var options = typeInfo.Options;
        foreach (var pair in overrides)
        {
            var property = typeInfo.Properties.FirstOrDefault(candidate =>
                string.Equals(candidate.Name, pair.Key, StringComparison.OrdinalIgnoreCase));
            var key = property?.Name ?? pair.Key;
            merged[key] = MergeValue(pair.Value, property, options);
        }
        return merged;
    }

    private static JsonNode? MergeValue(
        JsonNode? overlay, JsonPropertyInfo? property, JsonSerializerOptions options) =>
        overlay switch
        {
            JsonObject overlayObject when property?.PropertyType is { } objectType &&
                DefaultsObject(objectType, options) is { } objectDefaults =>
                MergeWithDefaults(overlayObject, options.GetTypeInfo(objectType), objectDefaults),
            JsonArray overlayArray when property?.PropertyType is { } collectionType &&
                options.GetTypeInfo(collectionType) is { ElementType: { } elementType } =>
                MergeArrayElements(overlayArray, elementType, options),
            _ => overlay?.DeepClone()
        };

    private static JsonArray MergeArrayElements(
        JsonArray overlay, Type elementType, JsonSerializerOptions options)
    {
        var elements = new JsonArray();
        foreach (var element in overlay)
        {
            elements.Add(element is JsonObject elementObject &&
                DefaultsObject(elementType, options) is { } elementDefaults
                    ? MergeWithDefaults(elementObject, options.GetTypeInfo(elementType),
                        elementDefaults)
                    : element?.DeepClone());
        }
        return elements;
    }

    // Every nested payload type DefaultsObject can instantiate: the schema is
    // fixed, so a statically reachable `new()` keeps each parameterless ctor
    // under trimming. JsonTypeInfo.CreateObject is null for all of them (init-only
    // members route deserialization through parameterized creators) and
    // Activator.CreateInstance(Type) fails trim analysis (IL2067) because the
    // runtime Type carries no DynamicallyAccessedMembers annotation.
    private static readonly Dictionary<Type, Func<object>> DefaultFactories = new()
    {
#if BUNDLER_HOST_LINUX
        [typeof(AppImageFileEntry)] = static () => new AppImageFileEntry(),
#endif
        [typeof(ArchiveFileEntry)] = static () => new ArchiveFileEntry(),
        [typeof(DebFileEntry)] = static () => new DebFileEntry(),
        [typeof(MacAppContentConfiguration)] = static () => new MacAppContentConfiguration(),
        [typeof(MacAppDocumentTypeConfiguration)] = static () => new MacAppDocumentTypeConfiguration(),
        [typeof(MacAppSigningConfiguration)] = static () => new MacAppSigningConfiguration(),
        [typeof(MacAppUrlTypeConfiguration)] = static () => new MacAppUrlTypeConfiguration(),
#if BUNDLER_HOST_MACOS
        [typeof(MacDmgSigningConfiguration)] = static () => new MacDmgSigningConfiguration(),
        [typeof(MacPkgPayloadItem)] = static () => new MacPkgPayloadItem(),
        [typeof(MacPkgSigningConfiguration)] = static () => new MacPkgSigningConfiguration(),
#endif
        [typeof(NsisShortcutConfiguration)] = static () => new NsisShortcutConfiguration(),
        [typeof(RpmFileEntry)] = static () => new RpmFileEntry(),
        [typeof(AlpineApkFileEntry)] = static () => new AlpineApkFileEntry(),
    };

    private static JsonObject? DefaultsObject(Type type, JsonSerializerOptions options)
    {
        if (options.GetTypeInfo(type) is not { Kind: JsonTypeInfoKind.Object } info)
        {
            return null;
        }
        return DefaultFactories.TryGetValue(type, out var createDefault)
            ? (JsonObject)JsonSerializer.SerializeToNode(createDefault(), info)!
            : throw new InvalidOperationException($"No default factory for {type.Name}.");
    }

    private static JsonTypeInfo TypeInfoFor<T>() =>
        BundlerJsonContext.Default.GetTypeInfo(typeof(T))
        ?? throw new InvalidOperationException($"No JSON metadata for {typeof(T).Name}.");

    private static IReadOnlyList<PackageFormat> FormatList(JsonNode? node, string rid)
    {
        if (node is JsonValue single && single.TryGetValue<string>(out var s))
        {
            return CliProgram.ParseFormats(s, rid);
        }
        if (node is JsonArray arr)
        {
            var names = arr.OfType<JsonValue>()
                .Select(v => v.TryGetValue<string>(out var s) ? s! : "")
                .Where(s => s.Length > 0).ToArray();
            return CliProgram.ParseFormats(string.Join(",", names), rid);
        }
        return [];
    }

    private static T? Section<T>(JsonObject document, string name) where T : class, new() =>
        document[name] is JsonObject child
            ? DeserializeWithDefaults(child, TypeInfoFor<T>(), new T())
            : null;

    private static string FormatName(PackageFormat format) => format switch
    {
        PackageFormat.TarGz => "targz",
        _ => format.ToString().ToLowerInvariant()
    };
}
