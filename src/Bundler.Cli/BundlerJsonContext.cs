using System.Text.Json;
using System.Text.Json.Serialization;
using DotNet.Bundler;
using DotNet.Bundler.AppImage;
using DotNet.Bundler.Archive;
using DotNet.Bundler.Deb;
using DotNet.Bundler.MacApp;
using DotNet.Bundler.MacDmg;
using DotNet.Bundler.MacPkg;
using DotNet.Bundler.Nsis;
using DotNet.Bundler.Rpm;
using DotNet.Bundler.Wix;

namespace DotNet.Bundler.Cli;

// Source-generated metadata for AOT: reflection-based (de)serialization is not
// available in native builds, so every bundler.json payload type is rooted here.
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    PropertyNameCaseInsensitive = true,
    ReadCommentHandling = JsonCommentHandling.Skip,
    AllowTrailingCommas = true)]
[JsonSerializable(typeof(BundleResourceConfiguration))]
[JsonSerializable(typeof(BundleFileAssociationConfiguration))]
[JsonSerializable(typeof(BundleUrlProtocolConfiguration))]
[JsonSerializable(typeof(NsisBundleConfiguration))]
[JsonSerializable(typeof(WixBundleConfiguration))]
[JsonSerializable(typeof(MacAppBundleConfiguration))]
[JsonSerializable(typeof(MacDmgBundleConfiguration))]
[JsonSerializable(typeof(MacPkgBundleConfiguration))]
[JsonSerializable(typeof(DebBundleConfiguration))]
[JsonSerializable(typeof(RpmBundleConfiguration))]
[JsonSerializable(typeof(AppImageBundleConfiguration))]
[JsonSerializable(typeof(ArchiveBundleConfiguration))]
internal partial class BundlerJsonContext : JsonSerializerContext;
