using System.Text.Json;
using System.Text.Json.Serialization;
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

// Source-generated metadata for AOT: reflection-based (de)serialization is not
// available in native builds, so every bundler.json payload type is rooted here.
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    PropertyNameCaseInsensitive = true,
    ReadCommentHandling = JsonCommentHandling.Skip,
    AllowTrailingCommas = true,
    Converters = [typeof(JsonStringEnumConverter<PackageFormat>)])]
[JsonSerializable(typeof(BundleConfiguration))]
[JsonSerializable(typeof(BundleResourceConfiguration))]
[JsonSerializable(typeof(BundleFileAssociationConfiguration))]
[JsonSerializable(typeof(BundleUrlProtocolConfiguration))]
[JsonSerializable(typeof(NsisBundleConfiguration))]
#if BUNDLER_HOST_WINDOWS
[JsonSerializable(typeof(WixBundleConfiguration))]
#endif
[JsonSerializable(typeof(MacAppBundleConfiguration))]
#if BUNDLER_HOST_MACOS
[JsonSerializable(typeof(MacDmgBundleConfiguration))]
[JsonSerializable(typeof(MacPkgBundleConfiguration))]
#endif
[JsonSerializable(typeof(DebBundleConfiguration))]
[JsonSerializable(typeof(RpmBundleConfiguration))]
#if BUNDLER_HOST_LINUX
[JsonSerializable(typeof(AppImageBundleConfiguration))]
#endif
[JsonSerializable(typeof(ArchiveBundleConfiguration))]
[JsonSerializable(typeof(AlpineApkBundleConfiguration))]
[JsonSerializable(typeof(UpdateBundleConfiguration))]
internal partial class BundlerJsonContext : JsonSerializerContext;
