using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization.Metadata;
using DotNet.Bundler;
using DotNet.Bundler.AlpineApk;
using DotNet.Bundler.Core;
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
        "archive", "alpineapk", "update"
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
        return Materialize(document, parsed, baseDirectory);
    }

    // Schema is fixed: unknown keys (typos) are rejected rather than ignored.
    private static readonly HashSet<string> TopLevelFields = new(StringComparer.Ordinal)
    {
        "productName", "identifier", "version", "publisher", "description", "homepage",
        "copyright", "licenseFile", "outputDirectory", "icons", "resources",
        "fileAssociations", "urlProtocols", "targets"
    };

    // 全平台格式分节名（不分宿主）：写错宿主的节给"本宿主不支持"而不是 Unknown-key。
    private static readonly HashSet<string> AllFormatSections = new(StringComparer.Ordinal)
    {
        "nsis", "msi", "app", "dmg", "pkg", "deb", "rpm", "appimage",
        "archive", "alpineapk"
    };

    private static readonly HashSet<string> TargetFields_Exact = new(StringComparer.Ordinal)
    {
        "runtimeIdentifier", "inputDirectory", "mainExecutable", "signingFiles", "formats"
    };

    private static readonly HashSet<string> TargetFields_Allowed = new(StringComparer.OrdinalIgnoreCase)
    {
        "runtimeIdentifier", "inputDirectory", "mainExecutable", "signingFiles", "formats"
    };

    // *File/*Path/*Directory 后缀之外仍指宿主路径的旋钮：显式清单，与各
    // *BundleConfiguration 的真实属性名对齐，随配置面扩充维护。值可为标量、
    // 字符串数组（extensionFragments/frameworks）或字符串字典（localeFiles）。
    private static readonly Dictionary<string, HashSet<string>> PathKnobs = new(StringComparer.Ordinal)
    {
        ["nsis"] = new(StringComparer.OrdinalIgnoreCase)
            { "installerIcon", "uninstallerIcon", "headerImage", "sidebarImage",
              "uninstallerHeaderImage", "installerHooks", "customLanguageFiles", "icon" },
        ["msi"] = new(StringComparer.OrdinalIgnoreCase)
            { "bannerBitmap", "dialogBitmap", "expertTemplate", "extensionFragments",
              "expertMergeModules", "localeFiles" },
        ["app"] = new(StringComparer.OrdinalIgnoreCase)
            { "frameworks" },
        ["alpineapk"] = new(StringComparer.OrdinalIgnoreCase)
            { "preInstallScript", "postInstallScript", "preDeinstallScript",
              "postDeinstallScript", "preUpgradeScript", "postUpgradeScript" },
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
        ["alpineapk"] = typeof(AlpineApkBundleConfiguration),
        // 更新面不是格式分节，但同样走节解析与 --update.<knob> 覆盖。
        ["update"] = typeof(UpdateBundleConfiguration)
    };

    private static void EnforceSchema(JsonObject document)
    {
        foreach (var key in document.Select(pair => pair.Key).ToArray())
        {
            if (TopLevelFields.Contains(key) || SectionTypes.ContainsKey(key))
            {
                continue;
            }
            var canonical = TopLevelFields.Concat(SectionTypes.Keys)
                .FirstOrDefault(known => string.Equals(known, key, StringComparison.OrdinalIgnoreCase));
            if (canonical is not null)
            {
                // 大小写变体过门禁后会被大小写敏感的取值静默丢弃：直接点破正确写法。
                throw new CliUsageException(
                    $"Unknown configuration key '{key}'; did you mean '{canonical}'?");
            }
            if (AllFormatSections.Contains(key.ToLowerInvariant()))
            {
                throw new CliUsageException(
                    $"The '{key}' format section is not supported on this host.");
            }
            throw new CliUsageException(
                $"Unknown configuration key '{key}'. Known top-level fields: " +
                string.Join(", ", TopLevelFields.Order()) +
                "; format sections: " + string.Join(", ", SectionTypes.Keys.Order()) + ".");
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
                    if (!TargetFields_Exact.Contains(key))
                    {
                        var canonical = TargetFields_Exact.FirstOrDefault(k =>
                            string.Equals(k, key, StringComparison.OrdinalIgnoreCase));
                        if (canonical is not null)
                        {
                            throw new CliUsageException(
                                $"Unknown target key 'targets[{i}].{key}'; did you mean '{canonical}'?");
                        }
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
            var knownExact = (BundlerJsonContext.Default.GetTypeInfo(type)?.Properties
                    ?? throw new InvalidOperationException(
                        $"No JSON metadata for {type.Name}."))
                .Select(p => p.Name)
                .ToHashSet(StringComparer.Ordinal);
            var knownFolded = new HashSet<string>(knownExact, StringComparer.OrdinalIgnoreCase);
            foreach (var key in child.Select(pair => pair.Key).ToArray())
            {
                // 大小写变体会被后续大小写敏感取值静默丢弃：门禁直接点破正确写法。
                if (!knownExact.Contains(key))
                {
                    var canonical = knownExact.FirstOrDefault(k =>
                        string.Equals(k, key, StringComparison.OrdinalIgnoreCase));
                    if (canonical is not null)
                    {
                        throw new CliUsageException(
                            $"Unknown key '{section}.{key}'; did you mean '{canonical}'?");
                    }
                    throw new CliUsageException(
                        $"Unknown key '{section}.{key}'. Known {section} knobs: " +
                        string.Join(", ", knownFolded.Order()) + ".");
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
                    throw new CliUsageException(AllFormatSections.Contains(section)
                        ? $"The '--{section}' format section is not supported on this host."
                        : $"Unknown format section '--{section}'. Supported: {string.Join(", ", FormatSections.Order())}.");
                }
                var child = document[section] as JsonObject ?? new JsonObject();
                var knobName = KnobName(knob);
                // CLI 覆盖值相对当前工作目录解析，不走配置文件的 baseDirectory；
                // 路径旋钮不过 ParseValue 的数值强转（"00123"/"true" 是合法文件名）。
                var sectionValue = IsPathKnob(section, knobName) ? ParsePathValue(value) : ParseValue(value);
                child[knobName] = AsCollectionElement(
                    IsPathKnob(section, knobName) ? ResolveOverridePaths(sectionValue) : sectionValue,
                    SectionTypes[section], knobName);
                document[section] = child;
                continue;
            }
            var fieldName = FieldName(name);
            var overrideValue = SharedPathFields.Contains(fieldName) ? ParsePathValue(value) : ParseValue(value);
            document[fieldName] = AsCollectionElement(
                SharedPathFields.Contains(fieldName) ? ResolveOverridePaths(overrideValue) : overrideValue,
                typeof(BundleConfiguration), fieldName);
        }
    }

    // 逗号在 ParseValue 里是列表分隔符——路径类覆盖值先按 JSON 形状解析，
    // 再对每个叶串逐项判 rooted：`--icons=a.png,b.png` 成数组逐项解析；
    // `--msi.locale-files={"en":"a.wxl","de":"b.wxl"}` 这类 JSON 字典/数组
    // 内的路径同样按工作目录解析，逗号不再把 JSON 拆开。
    // 路径旋钮专用：不做数字/布尔强转——"00123"/"true" 是合法文件名；
    // JSON 容器仍按形状解析（叶串原样），逗号列表按原文逐项拆。
    private static JsonNode? ParsePathValue(string value)
    {
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

    private static JsonNode? ResolveOverridePaths(JsonNode? node)
    {
        switch (node)
        {
            case JsonValue scalar when scalar.TryGetValue<string>(out var path) && path.Length > 0:
                return JsonValue.Create(Path.IsPathRooted(path) ? path : Path.GetFullPath(path));
            // 路径串恰为数字/布尔字面量（文件 "123"、"true"）时被 ParseValue
            // 收成了非字符串 JsonValue——还原成串再按 cwd 解析，否则反序列化失败。
            case JsonValue scalar when scalar.GetValueKind() is
                JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False:
                return JsonValue.Create(Path.GetFullPath(scalar.ToJsonString()));
            case JsonArray list:
                for (var i = 0; i < list.Count; i++)
                {
                    list[i] = ResolveOverridePaths(list[i]);
                }
                return list;
            case JsonObject obj:
                foreach (var key in obj.Select(pair => pair.Key).ToArray())
                {
                    obj[key] = ResolveOverridePaths(obj[key]);
                }
                return obj;
            default:
                return node;
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

    // 顶层共享字段里指宿主路径的旋钮；targets 级 inputDirectory 在 Materialize 单列。
    private static readonly HashSet<string> SharedPathFields = new(StringComparer.Ordinal)
    {
        "licenseFile", "outputDirectory", "icons"
    };

    private static bool IsPathKnob(string section, string knob) =>
        knob.EndsWith("File", StringComparison.OrdinalIgnoreCase) ||
        knob.EndsWith("Path", StringComparison.OrdinalIgnoreCase) ||
        knob.EndsWith("Directory", StringComparison.OrdinalIgnoreCase) ||
        PathKnobs.TryGetValue(section, out var knobs) && knobs.Contains(knob);

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

    // 列表型旋钮的单值覆盖（--icons=a.png、--deb.depends=libc6）先包一层 JsonArray，
    // 否则标量进 IReadOnlyList 字段在反序列化时以 JsonException 炸成 rc=1。
    private static JsonNode? AsCollectionElement(JsonNode? node, Type owner, string propertyName)
    {
        if (node is null or JsonArray)
        {
            return node;
        }
        var propertyType = BundlerJsonContext.Default.GetTypeInfo(owner)?.Properties
            .FirstOrDefault(candidate =>
                string.Equals(candidate.Name, propertyName, StringComparison.Ordinal))
            ?.PropertyType;
        return propertyType is not null && IsJsonCollection(propertyType)
            ? new JsonArray(node)
            : node;
    }

    private static bool IsJsonCollection(Type type) =>
        type != typeof(string) &&
        !typeof(System.Collections.IDictionary).IsAssignableFrom(type) &&
        !(type.IsGenericType &&
            type.GetGenericTypeDefinition() is { } generic &&
            (generic == typeof(IReadOnlyDictionary<,>) || generic == typeof(IDictionary<,>))) &&
        typeof(System.Collections.IEnumerable).IsAssignableFrom(type);

    // 共享字段的路径解析在反序列化后由 BundleConfigurationPaths.Resolve 统一完成，
    // 这里只剩各格式分节（update 也走共享面，跳过）。递归走查：标量按 *File/
    // *Path/*Directory 后缀、PathKnobs 显式表或 source 键判定；路径型数组/字典逐
    // 元素解析；嵌套对象（signing/shortcuts）与条目数组（files/payloadItems/
    // contents）递归——同一套判定在每个层级生效。
    private static void ResolveRelativePaths(JsonObject document, string baseDirectory)
    {
        static string Resolve(string baseDir, string path) =>
            Path.IsPathRooted(path) ? path : Path.GetFullPath(path, baseDir);

        foreach (var section in FormatSections.Where(name => name != "update"))
        {
            if (document[section] is JsonObject child)
            {
                ResolveSection(child, section);
            }
        }

        void ResolveSection(JsonObject node, string section)
        {
            foreach (var key in node.Select(pair => pair.Key).ToArray())
            {
                var value = node[key];
                var isPath = key.EndsWith("File", StringComparison.OrdinalIgnoreCase) ||
                    key.EndsWith("Path", StringComparison.OrdinalIgnoreCase) ||
                    key.EndsWith("Directory", StringComparison.OrdinalIgnoreCase) ||
                    key.Equals("source", StringComparison.OrdinalIgnoreCase) ||
                    PathKnobs.TryGetValue(section, out var knobs) && knobs.Contains(key);
                switch (value)
                {
                    case JsonValue scalar when isPath &&
                        scalar.TryGetValue<string>(out var path) && path.Length > 0:
                        node[key] = Resolve(baseDirectory, path);
                        break;
                    case JsonArray list when isPath:
                        for (var i = 0; i < list.Count; i++)
                        {
                            if (list[i] is JsonValue element &&
                                element.TryGetValue<string>(out var p) && p.Length > 0)
                            {
                                list[i] = Resolve(baseDirectory, p);
                            }
                        }
                        break;
                    case JsonObject dictionary when isPath:
                        foreach (var dictKey in dictionary.Select(pair => pair.Key).ToArray())
                        {
                            if (dictionary[dictKey] is JsonValue element &&
                                element.TryGetValue<string>(out var p) && p.Length > 0)
                            {
                                dictionary[dictKey] = Resolve(baseDirectory, p);
                            }
                        }
                        break;
                    case JsonObject nested:
                        ResolveSection(nested, section);
                        break;
                    case JsonArray entries:
                        foreach (var entry in entries.OfType<JsonObject>())
                        {
                            ResolveSection(entry, section);
                        }
                        break;
                }
            }
        }
    }

    private static CliResolvedConfiguration Materialize(
        JsonObject document,
        CliArguments parsed,
        string baseDirectory)
    {
        var targetsNode = document["targets"] as JsonArray ?? new JsonArray();
        // 合成数组必须挂回文档——共享反序列化只看 document["targets"]。
        document["targets"] ??= targetsNode;
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
                // CLI 输入相对当前工作目录解析，不走配置文件的 baseDirectory。
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

        // targets[].formats 归一成枚举名数组再走共享反序列化：ParseFormats 顺带
        // 做 rid×format 矩阵校验并吃 "a,b"/"a;b" 串形，source-gen 侧不再另写规则。
        foreach (var node in targetsNode.OfType<JsonObject>())
        {
            node["formats"] = new JsonArray(
                FormatList(node["formats"], TargetRid(node, parsed))
                    .Select(f => (JsonNode?)JsonValue.Create(FormatName(f))).ToArray());
        }

        // 显式 null 视为未写：剥掉后让 defaults 基线生效——与原手写物化语义一致
        // （"icons": null 曾等于 []，"outputDirectory": null 曾等于 "bundler-out"）。
        foreach (var key in document.Where(pair => pair.Value is null).Select(pair => pair.Key).ToArray())
        {
            document.Remove(key);
        }

        // 共享字段与 Core 的加载管线同一来源：反序列化模型 + 共用
        // BundleConfigurationPaths.Resolve，新增共享字段只改模型一侧。
        var bundle = BundleConfigurationPaths.Resolve(
            DeserializeWithDefaults(
                document,
                TypeInfoFor<BundleConfiguration>(),
                new BundleConfiguration { OutputDirectory = "bundler-out" }),
            baseDirectory);

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
        // 共享 BundleConfiguration 的嵌套负载型——DeserializeWithDefaults 走全文档合并时会到。
        [typeof(BundleTargetConfiguration)] = static () => new BundleTargetConfiguration(),
        [typeof(BundleResourceConfiguration)] = static () => new BundleResourceConfiguration(),
        [typeof(BundleFileAssociationConfiguration)] = static () => new BundleFileAssociationConfiguration(),
        [typeof(BundleUrlProtocolConfiguration)] = static () => new BundleUrlProtocolConfiguration(),
        [typeof(UpdateBundleConfiguration)] = static () => new UpdateBundleConfiguration(),
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
