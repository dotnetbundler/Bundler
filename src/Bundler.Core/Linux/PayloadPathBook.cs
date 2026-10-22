namespace DotNet.Bundler.Core;

/// <summary>
/// Claim bookkeeping for Linux package payloads (deb/rpm/apk): every archive
/// path — file, symlink, or explicit directory — registers here so that two
/// entries cannot silently land on the same path and a file cannot sit on a
/// path where a directory (or vice versa) is already claimed. Posix-style
/// <c>/</c> paths throughout; directories are stored without a trailing slash.
/// </summary>
internal sealed class PayloadPathBook
{
    private readonly HashSet<string> _leaves = new(StringComparer.Ordinal);
    private readonly HashSet<string> _directories = new(StringComparer.Ordinal);
    private readonly Func<string, bool>? _sharedBoundary;
    private readonly Func<string, bool>? _systemLeaf;
    private readonly Action<string>? _onNewDirectory;

    /// <param name="sharedBoundary">
    /// Optional predicate for "system" directories that the package does not
    /// own but shares with others (rpm's <c>/usr/bin</c> etc.): directory
    /// prefix claims stop there without registering. It deliberately does not
    /// apply to leaf claims — a file living <em>under</em> a shared tree is a
    /// normal payload member.
    /// </param>
    /// <param name="systemLeaf">
    /// Optional predicate rejecting a leaf claim whose exact path is a shared
    /// system directory (a file can never occupy e.g. <c>/usr/bin</c> itself).
    /// </param>
    /// <param name="onNewDirectory">
    /// Fired for each newly claimed directory (parents before children) —
    /// backends that emit explicit directory entries append them there.
    /// </param>
    internal PayloadPathBook(
        Func<string, bool>? sharedBoundary = null,
        Func<string, bool>? systemLeaf = null,
        Action<string>? onNewDirectory = null)
    {
        _sharedBoundary = sharedBoundary;
        _systemLeaf = systemLeaf;
        _onNewDirectory = onNewDirectory;
    }

    /// <summary>Claimed directory paths (excluding shared-boundary prefixes).</summary>
    internal IEnumerable<string> Directories => _directories;

    /// <summary>
    /// Registers a file/symlink leaf at <paramref name="archivePath"/>: its
    /// parent chain is claimed as directories, and the leaf itself must not
    /// collide with an existing leaf, directory, or system-directory path.
    /// </summary>
    internal void ClaimFile(string archivePath)
    {
        var slash = archivePath.LastIndexOf('/');
        ClaimDirectory(slash > 0 ? archivePath.Substring(0, slash) : null);
        if (_directories.Contains(archivePath) || _systemLeaf?.Invoke(archivePath) == true)
        {
            throw new InvalidOperationException(
                $"The payload path '{archivePath}' is already occupied by a directory — " +
                "a file cannot be placed on it.");
        }
        if (!_leaves.Add(archivePath))
        {
            throw new InvalidOperationException(
                $"The payload path '{archivePath}' is claimed twice — " +
                "duplicate input or conflicting package file mapping.");
        }
    }

    /// <summary>
    /// Registers <paramref name="directoryPath"/> and every missing prefix of it
    /// as directories. A prefix already occupied by a leaf (file/symlink)
    /// rejects. Newly claimed directories fire the constructor callback in
    /// creation order.
    /// </summary>
    internal void ClaimDirectory(string? directoryPath)
    {
        if (directoryPath is null)
        {
            return;
        }
        var path = directoryPath.TrimEnd('/');
        if (path.Length == 0)
        {
            return;
        }
        // Deepest prefix first-collect then register root→leaf so onNewDirectory
        // fires in creation order (parents before children).
        var prefixes = new List<string>();
        for (var p = path; p.Length > 0;)
        {
            if (_sharedBoundary?.Invoke(p) == true || _directories.Contains(p) || _leaves.Contains(p))
            {
                if (_leaves.Contains(p))
                {
                    throw new InvalidOperationException(
                        $"The payload directory '{p}' is already occupied by a file — " +
                        "a directory cannot be placed on it.");
                }
                break; // already owned directory or shared boundary — nothing above changes
            }
            prefixes.Add(p);
            var slash = p.LastIndexOf('/');
            p = slash > 0 ? p.Substring(0, slash) : "";
        }
        for (var i = prefixes.Count - 1; i >= 0; i--)
        {
            _directories.Add(prefixes[i]);
            _onNewDirectory?.Invoke(prefixes[i]);
        }
    }
}
