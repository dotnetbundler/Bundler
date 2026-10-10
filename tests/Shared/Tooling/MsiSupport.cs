// MSI 集成腿公共件：fixture 外拷还原、msiexec、DTF 数据库查询、还原契约断言。
// 对应原 MsiTestSupport.ps1 + AssertLocalRestore.ps1（还原段）。
using System.Text.Json;
using WixToolset.Dtf.WindowsInstaller;

internal static class MsiSupport
{
    // 对应 Pack-MsiTestPackages：pack 前先杀常驻 MSBuild 节点（nodeReuse 文件锁根因）。
    public static string PackPackages(string packageDir)
    {
        Dotnet.ShutdownBuildServers();
        var version = RepositoryLayout.PackageVersion;
        ProcessRunner.AssertSuccess(
            Dotnet.Run(["pack", "Bundler.slnx", "-c", "Release", "-o", packageDir,
                $"-p:BundlerPackageVersion={version}"],
                new ProcessRunner.Options { WorkingDirectory = RepositoryLayout.Root }),
            "dotnet pack failed");
        foreach (var name in new[] { "DotNet.Bundler", "DotNet.Bundler.Wix", "DotNet.Bundler.MSBuild" })
        {
            Dotnet.AssertPackageExists(packageDir, name, version);
        }
        AssertWixPackageLicensedSources(packageDir, version);
        return version;
    }

    // Wix 包的随包许可证/溯源文件必须逐字节与仓库源一致。
    private static void AssertWixPackageLicensedSources(string packageDir, string version)
    {
        var pkg = Path.Combine(packageDir, $"DotNet.Bundler.Wix.{version}.nupkg");
        var entries = Dotnet.NupkgEntries(pkg);
        var required = new[]
        {
            "lib/netstandard2.0/DotNet.Bundler.Wix.dll", "licenses/wix/LICENSE.TXT",
            "licenses/wix/wix3141-source.zip", "licenses/wix/msi-wix-provenance.md",
            "licenses/wix/SHA256SUMS", "THIRD-PARTY-NOTICES.md",
        };
        foreach (var entry in required)
        {
            Assert.Contains(entries, e => e == entry);
        }
        var sources = new Dictionary<string, string>
        {
            ["licenses/wix/LICENSE.TXT"] = "third_party/wix/LICENSE.TXT",
            ["licenses/wix/wix3141-source.zip"] = "third_party/wix/wix3141-source.zip",
            ["licenses/wix/msi-wix-provenance.md"] = "third_party/wix/msi-wix-provenance.md",
            ["licenses/wix/SHA256SUMS"] = "third_party/wix/SHA256SUMS",
            ["THIRD-PARTY-NOTICES.md"] = "THIRD-PARTY-NOTICES.md",
        };
        using var archive = System.IO.Compression.ZipFile.OpenRead(pkg);
        foreach (var (entryName, repoRel) in sources)
        {
            var entry = archive.GetEntry(entryName);
            Assert.NotNull(entry);
            using var stream = entry!.Open();
            var actual = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(stream));
            var expected = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(
                File.ReadAllBytes(Path.Combine(RepositoryLayout.Root, repoRel))));
            Assert.Equal(expected, actual);
        }
    }

    // 对应 Copy-MsiTestFixture：把 smoke fixture 拷出仓库当外部包消费项目。
    public static string CopyFixture(string destination, string fixtureSource)
    {
        Assert.False(Directory.Exists(destination),
            $"Refusing to replace an existing test fixture: {destination}");
        Directory.CreateDirectory(destination);
        foreach (var name in new[] { "BundlerMsiSmoke.csproj", "Program.cs" })
        {
            File.Copy(Path.Combine(fixtureSource, name), Path.Combine(destination, name));
        }
        File.Copy(Path.Combine(RepositoryLayout.Root, "Bundler.LocalPackages.props"),
            Path.Combine(destination, "Bundler.LocalPackages.props"));
        CopyDirectory(Path.Combine(fixtureSource, "Assets"), Path.Combine(destination, "Assets"));
        return Path.Combine(destination, "BundlerMsiSmoke.csproj");
    }

    private static void CopyDirectory(string source, string destination)
    {
        Directory.CreateDirectory(destination);
        foreach (var file in Directory.EnumerateFiles(source))
        {
            File.Copy(file, Path.Combine(destination, Path.GetFileName(file)));
        }
        foreach (var dir in Directory.EnumerateDirectories(source))
        {
            CopyDirectory(dir, Path.Combine(destination, Path.GetFileName(dir)));
        }
    }

    public static void RestoreFixture(string project, string packageDir, string cache,
        string version, string? target = null)
    {
        var config = PinnedPackageSource.WriteConfig(
            Path.Combine(Path.GetDirectoryName(Path.GetFullPath(cache))!, "nuget-pin"),
            packageDir);
        var args = new List<string>
        {
            "restore", project, "--configfile", config,
            $"-p:BundlerPackageSource={packageDir}",
            $"-p:RestorePackagesPath={cache}",
            $"-p:BundlerPackageVersion={version}",
        };
        if (target is not null)
        {
            args.Add($"-p:BundlerTarget={target}");
        }
        ProcessRunner.AssertSuccess(Dotnet.Run(args, new ProcessRunner.Options()),
            $"MSI fixture restore failed: {project}");
        AssertLocalBundlerRestore(project, version, packageDir, cache,
            ["DotNet.Bundler", "DotNet.Bundler.MSBuild", "DotNet.Bundler.Wix"]);
    }

    // 契约实现统一收在 LocalRestoreAssert.Verify——这里只做签名转发。
    public static void AssertLocalBundlerRestore(string project, string version,
        string source, string cache, string[] requiredPackages)
        => LocalRestoreAssert.Verify(project, version, source, cache, requiredPackages);

    // Windows Installer 是整机单例服务：并行测试类并发调用或外部安装会撞 1618。
    // 进程内串行覆盖本类竞态，1618 有界重试覆盖仓外安装占用。
    private static readonly object MsiexecGate = new();

    public static ProcessRunner.Result Msiexec(string argumentLine)
    {
        var path = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.Windows), "System32", "msiexec.exe");
        const int maxAttempts = 4;
        for (var attempt = 1; ; attempt++)
        {
            ProcessRunner.Result result;
            lock (MsiexecGate)
            {
                result = ProcessRunner.Run(path, argumentLine,
                    new ProcessRunner.Options { Timeout = TimeSpan.FromMinutes(10) });
            }
            if (result.ExitCode != 1618 || attempt == maxAttempts)
            {
                return result;
            }
            Thread.Sleep(TimeSpan.FromSeconds(15));
        }
    }

    public static void MsiexecExpect(string argumentLine, params int[] allowed)
    {
        var result = Msiexec(argumentLine);
        Assert.True(allowed.Contains(result.ExitCode),
            $"msiexec returned {result.ExitCode}, expected one of [{string.Join(", ", allowed)}]: {argumentLine}\n{result.StdErr}");
    }

    public static string GetProperty(string msiPath, string name)
    {
        using var database = new Database(msiPath, DatabaseOpenMode.ReadOnly);
        var values = database.ExecuteStringQuery(
            $"SELECT `Value` FROM `Property` WHERE `Property` = '{name}'");
        Assert.True(values.Count > 0, $"MSI property {name} is missing: {msiPath}");
        return values[0];
    }

    public static string[] QueryColumn(string msiPath, string query)
    {
        using var database = new Database(msiPath, DatabaseOpenMode.ReadOnly);
        return database.ExecuteStringQuery(query).ToArray();
    }

    public static Database OpenWritable(string msiPath)
        => new(msiPath, DatabaseOpenMode.Direct);
}
