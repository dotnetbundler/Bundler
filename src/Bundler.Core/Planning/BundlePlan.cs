using Bundler.Core.Models;

namespace Bundler.Core.Planning;

public sealed record BundlePlan(IReadOnlyList<BundlePlanItem> Items);

public sealed record BundlePlanItem(
    BundleTarget Target,
    PackageFormat Format,
    string InputDirectory,
    string MainExecutable,
    string OutputDirectory,
    bool Intermediate);
