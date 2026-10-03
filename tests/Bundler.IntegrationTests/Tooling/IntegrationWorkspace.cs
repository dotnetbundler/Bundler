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
    public static IntegrationWorkspace Create(string slug, string identity)
    {
        var root = System.IO.Path.Combine(RepositoryLayout.ArtifactsRoot, slug);
        var marker = System.IO.Path.Combine(root, ".bundler-identity");
        if (Directory.Exists(root))
        {
            if (!File.Exists(marker) || File.ReadAllText(marker).Trim() != identity)
            {
                throw new InvalidOperationException(
                    $"{root} already exists and was not created by this test; refusing to touch it.");
            }
            Directory.Delete(root, recursive: true);
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
