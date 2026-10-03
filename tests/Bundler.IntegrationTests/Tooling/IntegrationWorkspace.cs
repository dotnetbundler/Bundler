using System.Collections.Concurrent;

// 集成测试工作区：每测试类一个 artifacts/<slug>-integration 根，带身份标记防误删；
// 等价于脚本的 "目录身份判定 + 退出清理"。
public sealed class IntegrationWorkspace : IDisposable
{
    public string Root { get; }

    private IntegrationWorkspace(string root)
    {
        Root = root;
    }

    // slug 用测试目录同名（archive-integration 等），保持产物位置与脚本时代一致。
    // identity 沿用脚本的 .bundler-identity 标记：目录已存在但标记不符时拒绝触碰。
    // 每 slug 一把进程级文件锁：第二个 dotnet test 进程若在第一个仍持有工作区时进来，
    // marker 匹配会走整目录 Delete 把活工作区删成空——之后 docker 腿挂载缺失源还会被
    // daemon 建成 root 属目录，级联 EACCES。锁把并发运行变成"等一下，不行就明说"。
    private static readonly ConcurrentDictionary<string, FileStream> Claims = new(StringComparer.Ordinal);
    private static readonly ConcurrentDictionary<string, byte> BusySlugs = new(StringComparer.Ordinal);

    private static void ClaimWorkspace(string slug)
    {
        var lockDir = System.IO.Path.Combine(RepositoryLayout.ArtifactsRoot, ".locks");
        Directory.CreateDirectory(lockDir);
        var lockPath = System.IO.Path.Combine(lockDir, slug + ".lock");
        // 同 slug 已判定忙过一次即不再等——避免每个事实各等一遍超时。
        if (BusySlugs.ContainsKey(slug))
        {
            Assert.Fail($"integration workspace '{slug}' is owned by another test process (lock: {lockPath}).");
        }
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(60);
        while (true)
        {
            try
            {
                Claims[slug] = new FileStream(lockPath, FileMode.OpenOrCreate,
                    FileAccess.ReadWrite, FileShare.None);
                return;
            }
            catch (IOException) when (DateTime.UtcNow < deadline)
            {
                Thread.Sleep(250);
            }
            catch (IOException)
            {
                BusySlugs[slug] = 1;
                Assert.Fail($"integration workspace '{slug}' is owned by another test process (lock: {lockPath}).");
            }
        }
    }

    public static IntegrationWorkspace Create(string slug, string identity)
    {
        ClaimWorkspace(slug);
        var root = System.IO.Path.Combine(RepositoryLayout.ArtifactsRoot, slug);
        var marker = System.IO.Path.Combine(root, ".bundler-identity");
        if (Directory.Exists(root))
        {
            if (!File.Exists(marker) || File.ReadAllText(marker).Trim() != identity)
            {
                // 外部/脚本时代残留目录不判死也不删——挪到旁名归档，既不冒充我们的产物也不挡测试。
                var aside = root + ".stale-" + Guid.NewGuid().ToString("N")[..8];
                Directory.Move(root, aside);
            }
            else
            {
                Directory.Delete(root, recursive: true);
            }
        }
        Directory.CreateDirectory(root);
        File.WriteAllText(marker, identity + "\n");
        return new IntegrationWorkspace(root);
    }

    public string Combine(params string[] parts) => System.IO.Path.Combine([Root, .. parts]);

    public void AssertUnder(string path) => RepositoryLayout.AssertUnderRoot(Root, path);

    public void Remove(string path)
    {
        AssertUnder(path);
        if (Directory.Exists(path))
        {
            Directory.Delete(path, recursive: true);
        }
        else if (File.Exists(path))
        {
            File.Delete(path);
        }
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(Root))
            {
                Directory.Delete(Root, recursive: true);
            }
        }
        catch (IOException)
        {
            // 遗留句柄/杀不掉的进程导致的残留不掩盖测试结论；产物目录属临时区。
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
