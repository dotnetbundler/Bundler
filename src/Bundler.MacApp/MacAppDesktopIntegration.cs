using System.Text.RegularExpressions;
using DotNet.Bundler;

namespace DotNet.Bundler.MacApp;

/// <summary>
/// CFBundleDocumentTypes/UTExportedTypeDeclarations, CFBundleURLTypes and
/// NSAppTransportSecurity emission, plus caller Info.plist merging with identity-key readback.
/// Mirrors tauri-bundler's macOS plist contract (snapshot 7dbfc1f).
/// </summary>
internal static class MacAppDesktopIntegration
{
    private static readonly Regex ExtensionPattern = new(
        "^[A-Za-z0-9][A-Za-z0-9_+-]{0,63}$", RegexOptions.Compiled);
    private static readonly Regex UrlSchemePattern = new(
        "^[A-Za-z][A-Za-z0-9+.-]{0,63}$", RegexOptions.Compiled);
    private static readonly Regex UtiPattern = new(
        "^[A-Za-z0-9-]+(\\.[A-Za-z0-9-]+)+$", RegexOptions.Compiled);

    private static readonly string[] IdentityKeys =
    [
        "CFBundleIdentifier", "CFBundleExecutable", "CFBundleShortVersionString",
        "CFBundleVersion", "CFBundlePackageType"
    ];

    internal sealed record ResolvedDocumentType(
        IReadOnlyList<string> Extensions,
        string? Name,
        string? Description,
        MacAppTypeRole Role,
        MacAppHandlerRank Rank,
        IReadOnlyList<string> ContentTypes,
        string? MimeType,
        string? ExportedTypeIdentifier,
        IReadOnlyList<string> ExportedTypeConformsTo);

    internal sealed record ResolvedUrlType(
        IReadOnlyList<string> Schemes, string? Name, MacAppTypeRole Role);

    internal static IReadOnlyList<ResolvedDocumentType> ResolveDocumentTypes(
        BundleConfiguration bundle, MacAppBundleConfiguration settings)
    {
        var resolved = new List<ResolvedDocumentType>();
        var consumed = new bool[bundle.FileAssociations.Count];
        var claimed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        for (var index = 0; index < settings.DocumentTypes.Count; index++)
        {
            var entry = settings.DocumentTypes[index];
            var display = $"documentTypes[{index}]";
            var extensions = NormalizeExtensions(entry.Extensions, display, claimed);
            var matches = MatchingSharedIndexes(bundle.FileAssociations, extensions, association => association.Extensions);
            if (entry.ContentTypes.Count == 0 && extensions.Length == 0 &&
                entry.ExportedTypeIdentifier is null)
            {
                throw new ArgumentException(
                    $"{display}: a document type needs extensions, content types, or an exported type identifier.");
            }
            if (entry.ExportedTypeIdentifier is not null)
            {
                RequireUti(entry.ExportedTypeIdentifier, $"{display}.exportedTypeIdentifier");
            }
            foreach (var conformsTo in entry.ExportedTypeConformsTo)
            {
                RequireUti(conformsTo, $"{display}.exportedTypeConformsTo");
            }

            string? name = entry.Name;
            string? description = entry.Description;
            string? mimeType = entry.MimeType;
            foreach (var sharedIndex in matches)
            {
                consumed[sharedIndex] = true;
                var shared = bundle.FileAssociations[sharedIndex];
                extensions = extensions
                    .Concat(shared.Extensions.Select(NormalizeExtension))
                    .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
                name ??= shared.Name;
                description ??= shared.Description;
                mimeType ??= shared.MimeType;
            }

            resolved.Add(new ResolvedDocumentType(
                extensions, name, description,
                entry.Role ?? MacAppTypeRole.Editor,
                entry.Rank ?? MacAppHandlerRank.Default,
                entry.ContentTypes, mimeType,
                entry.ExportedTypeIdentifier, entry.ExportedTypeConformsTo));
        }

        for (var index = 0; index < bundle.FileAssociations.Count; index++)
        {
            if (consumed[index])
            {
                continue;
            }
            var shared = bundle.FileAssociations[index];
            resolved.Add(new ResolvedDocumentType(
                shared.Extensions.Select(NormalizeExtension).ToArray(),
                shared.Name, shared.Description,
                MacAppTypeRole.Editor, MacAppHandlerRank.Default,
                [], shared.MimeType, null, []));
        }
        return resolved;
    }

    internal static IReadOnlyList<ResolvedUrlType> ResolveUrlTypes(
        BundleConfiguration bundle, MacAppBundleConfiguration settings)
    {
        var resolved = new List<ResolvedUrlType>();
        var consumed = new bool[bundle.UrlProtocols.Count];
        var claimed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        for (var index = 0; index < settings.UrlTypes.Count; index++)
        {
            var entry = settings.UrlTypes[index];
            var display = $"urlTypes[{index}]";
            var schemes = NormalizeSchemes(entry.Schemes, display, claimed);
            if (schemes.Length == 0)
            {
                throw new ArgumentException($"{display}: at least one URL scheme is required.");
            }
            var matches = MatchingSharedIndexes(bundle.UrlProtocols, schemes, protocol => protocol.Schemes);
            string? name = entry.Name;
            foreach (var sharedIndex in matches)
            {
                consumed[sharedIndex] = true;
                var shared = bundle.UrlProtocols[sharedIndex];
                schemes = schemes.Concat(shared.Schemes)
                    .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
                name ??= shared.Name;
            }
            resolved.Add(new ResolvedUrlType(schemes, name, entry.Role ?? MacAppTypeRole.Editor));
        }

        for (var index = 0; index < bundle.UrlProtocols.Count; index++)
        {
            if (consumed[index] || bundle.UrlProtocols[index].Schemes.Count == 0)
            {
                continue;
            }
            var shared = bundle.UrlProtocols[index];
            resolved.Add(new ResolvedUrlType(
                shared.Schemes, shared.Name, MacAppTypeRole.Editor));
        }
        return resolved;
    }

    internal static void EmitDocumentTypes(
        IDictionary<string, object> plist, IReadOnlyList<ResolvedDocumentType> documentTypes)
    {
        if (documentTypes.Count == 0)
        {
            return;
        }
        var exported = new List<object>();
        var documentEntries = new List<object>();
        foreach (var entry in documentTypes)
        {
            var documentType = new Dictionary<string, object>(StringComparer.Ordinal);
            if (entry.Extensions.Count > 0)
            {
                documentType["CFBundleTypeExtensions"] = entry.Extensions.Cast<object>().ToList();
            }
            var contentTypes = InferContentTypes(entry);
            if (contentTypes.Count > 0)
            {
                documentType["LSItemContentTypes"] = contentTypes.Cast<object>().ToList();
            }
            documentType["CFBundleTypeName"] =
                entry.Name ?? (entry.Extensions.Count > 0 ? entry.Extensions[0] : "");
            documentType["CFBundleTypeRole"] = entry.Role.ToString();
            documentType["LSHandlerRank"] = entry.Rank.ToString();
            documentEntries.Add(documentType);

            if (entry.ExportedTypeIdentifier is { } identifier)
            {
                var declaration = new Dictionary<string, object>(StringComparer.Ordinal)
                {
                    ["UTTypeIdentifier"] = identifier
                };
                if (entry.Description is { Length: > 0 } description)
                {
                    declaration["UTTypeDescription"] = description;
                }
                if (entry.ExportedTypeConformsTo.Count > 0)
                {
                    declaration["UTTypeConformsTo"] = entry.ExportedTypeConformsTo.Cast<object>().ToList();
                }
                var specification = new Dictionary<string, object>(StringComparer.Ordinal);
                if (entry.Extensions.Count > 0)
                {
                    specification["public.filename-extension"] = entry.Extensions.Cast<object>().ToList();
                }
                if (entry.MimeType is { Length: > 0 } mimeType)
                {
                    specification["public.mime-type"] = mimeType;
                }
                declaration["UTTypeTagSpecification"] = specification;
                exported.Add(declaration);
            }
        }
        if (exported.Count > 0)
        {
            plist["UTExportedTypeDeclarations"] = exported;
        }
        plist["CFBundleDocumentTypes"] = documentEntries;
    }

    internal static void EmitUrlTypes(
        IDictionary<string, object> plist,
        IReadOnlyList<ResolvedUrlType> urlTypes,
        string bundleIdentifier)
    {
        var entries = urlTypes
            .Where(urlType => urlType.Schemes.Count > 0)
            .Select(urlType => new Dictionary<string, object>(StringComparer.Ordinal)
            {
                ["CFBundleURLSchemes"] = urlType.Schemes.Cast<object>().ToList(),
                ["CFBundleURLName"] = urlType.Name ?? $"{bundleIdentifier} {urlType.Schemes[0]}",
                ["CFBundleTypeRole"] = urlType.Role.ToString()
            })
            .Cast<object>()
            .ToList();
        if (entries.Count > 0)
        {
            plist["CFBundleURLTypes"] = entries;
        }
    }

    internal static void EmitAppTransportSecurity(
        IDictionary<string, object> plist, string? exceptionDomain)
    {
        if (string.IsNullOrWhiteSpace(exceptionDomain))
        {
            return;
        }
        var domain = exceptionDomain!.Trim();
        plist["NSAppTransportSecurity"] = new Dictionary<string, object>(StringComparer.Ordinal)
        {
            ["NSExceptionDomains"] = new Dictionary<string, object>(StringComparer.Ordinal)
            {
                [domain] = new Dictionary<string, object>(StringComparer.Ordinal)
                {
                    ["NSExceptionAllowsInsecureHTTPLoads"] = true,
                    ["NSIncludesSubdomains"] = true
                }
            }
        };
    }

    internal static void MergeCallerPlist(
        IDictionary<string, object> plist, MacAppBundleConfiguration settings)
    {
        Dictionary<string, object> caller;
        if (settings.InfoPlistFile is { } file)
        {
            caller = InfoPlist.ReadDictionary(Path.GetFullPath(file), $"infoPlistFile '{file}'");
        }
        else if (settings.InfoPlistXml is { } xml)
        {
            caller = InfoPlist.ParseXml(xml, "infoPlistXml");
        }
        else
        {
            return;
        }
        foreach (var pair in caller)
        {
            plist[pair.Key] = pair.Value;
        }
    }

    internal static void EnforceIdentityKeys(
        IReadOnlyDictionary<string, object> plist, IReadOnlyDictionary<string, string> expected)
    {
        foreach (var key in IdentityKeys)
        {
            var expectedValue = expected[key];
            if (!plist.TryGetValue(key, out var merged) || !InfoPlist.TryToString(merged, out var actual) ||
                actual != expectedValue)
            {
                var rendered = plist.TryGetValue(key, out var value) ? DescribeValue(value) : "<missing>";
                throw new InvalidOperationException(
                    $"The merged Info.plist must keep {key} = '{expectedValue}' " +
                    $"(caller plist produced {rendered}); identity keys are owned by the bundle configuration.");
            }
        }
    }

    private static string DescribeValue(object? value) =>
        value is not null && InfoPlist.TryToString(value, out var rendered)
            ? $"'{rendered}'"
            : $"<{value?.GetType().Name ?? "null"}>";

    private static int[] MatchingSharedIndexes<T>(
        IReadOnlyList<T> shared, IReadOnlyList<string> wanted, Func<T, IReadOnlyList<string>> tokens)
    {
        var matches = new List<int>();
        for (var index = 0; index < shared.Count; index++)
        {
            if (tokens(shared[index]).Any(token =>
                    wanted.Contains(NormalizeExtension(token), StringComparer.OrdinalIgnoreCase)))
            {
                matches.Add(index);
            }
        }
        return matches.ToArray();
    }

    private static string[] NormalizeExtensions(
        IReadOnlyList<string> extensions, string display, HashSet<string> claimed)
    {
        var normalized = new List<string>();
        for (var index = 0; index < extensions.Count; index++)
        {
            var extension = NormalizeExtension(extensions[index]);
            if (!ExtensionPattern.IsMatch(extension))
            {
                throw new ArgumentException(
                    $"{display}.extensions[{index}]: must be a 1-64 character extension using letters, " +
                    $"digits, '_', '+' or '-'; got '{extensions[index]}'.");
            }
            if (!claimed.Add(extension))
            {
                throw new ArgumentException(
                    $"{display}.extensions[{index}]: extension '{extension}' is declared by more than one document type.");
            }
            normalized.Add(extension);
        }
        return normalized.ToArray();
    }

    private static string[] NormalizeSchemes(
        IReadOnlyList<string> schemes, string display, HashSet<string> claimed)
    {
        var normalized = new List<string>();
        for (var index = 0; index < schemes.Count; index++)
        {
            var scheme = (schemes[index] ?? "").Trim();
            if (!UrlSchemePattern.IsMatch(scheme))
            {
                throw new ArgumentException(
                    $"{display}.schemes[{index}]: must be a URI scheme beginning with a letter; got '{schemes[index]}'.");
            }
            if (!claimed.Add(scheme))
            {
                throw new ArgumentException(
                    $"{display}.schemes[{index}]: scheme '{scheme}' is declared by more than one URL type.");
            }
            normalized.Add(scheme);
        }
        return normalized.ToArray();
    }

    private static string NormalizeExtension(string extension) =>
        (extension ?? "").Trim().TrimStart('.');

    private static void RequireUti(string uti, string display)
    {
        if (string.IsNullOrWhiteSpace(uti) || !UtiPattern.IsMatch(uti.Trim()))
        {
            throw new ArgumentException(
                $"{display}: a UTI must look like 'public.data' or 'com.example.type'; got '{uti}'.");
        }
    }

    private static IReadOnlyList<string> InferContentTypes(ResolvedDocumentType entry)
    {
        if (entry.ExportedTypeIdentifier is { } exported)
        {
            return [exported];
        }
        var contentTypes = new List<string>(entry.ContentTypes);
        void Add(string? uti)
        {
            if (uti is not null && !contentTypes.Contains(uti, StringComparer.Ordinal))
            {
                contentTypes.Add(uti);
            }
        }
        foreach (var extension in entry.Extensions)
        {
            Add(ExtensionToUti(extension));
        }
        if (entry.MimeType is { } mimeType)
        {
            Add(MimeTypeToUti(mimeType));
        }
        return contentTypes;
    }

    private static string? ExtensionToUti(string extension) => extension.ToLowerInvariant() switch
    {
        "png" => "public.png",
        "jpg" or "jpeg" => "public.jpeg",
        "gif" => "com.compuserve.gif",
        "bmp" => "com.microsoft.bmp",
        "tiff" or "tif" => "public.tiff",
        "ico" => "com.microsoft.ico",
        "heic" or "heif" => "public.heif-standard-image",
        "webp" => "org.webmproject.webp",
        "svg" => "public.svg-image",
        "mp4" => "public.mpeg-4",
        "mov" => "com.apple.quicktime-movie",
        "avi" => "public.avi",
        "mkv" => "public.mpeg-4",
        "mp3" => "public.mp3",
        "wav" => "com.microsoft.waveform-audio",
        "aac" => "public.aac-audio",
        "m4a" => "public.mpeg-4-audio",
        "pdf" => "com.adobe.pdf",
        "txt" => "public.plain-text",
        "rtf" => "public.rtf",
        "html" or "htm" => "public.html",
        "json" => "public.json",
        "xml" => "public.xml",
        _ => null
    };

    private static string? MimeTypeToUti(string mimeType) => mimeType.Trim() switch
    {
        "image/png" => "public.png",
        "image/jpeg" or "image/jpg" => "public.jpeg",
        "image/gif" => "com.compuserve.gif",
        "image/bmp" => "com.microsoft.bmp",
        "image/tiff" => "public.tiff",
        "image/heic" or "image/heif" => "public.heif-standard-image",
        "image/webp" => "org.webmproject.webp",
        "image/svg+xml" => "public.svg-image",
        var mime when mime.StartsWith("image/", StringComparison.Ordinal) => "public.image",
        "video/mp4" => "public.mpeg-4",
        "video/quicktime" => "com.apple.quicktime-movie",
        "video/x-msvideo" => "public.avi",
        var mime when mime.StartsWith("video/", StringComparison.Ordinal) => "public.movie",
        "audio/mpeg" or "audio/mp3" => "public.mp3",
        "audio/wav" or "audio/wave" => "com.microsoft.waveform-audio",
        "audio/aac" => "public.aac-audio",
        "audio/mp4" => "public.mpeg-4-audio",
        var mime when mime.StartsWith("audio/", StringComparison.Ordinal) => "public.audio",
        "application/pdf" => "com.adobe.pdf",
        "text/plain" => "public.plain-text",
        "text/rtf" => "public.rtf",
        "text/html" => "public.html",
        "application/json" => "public.json",
        "application/xml" or "text/xml" => "public.xml",
        _ => null
    };
}
