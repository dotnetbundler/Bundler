// UPDATE 链集成腿：只能在真环境跑的更新断言（真小卷 ENOSPC、真机/凭证腿在外层 Special）。
using System.Text;

public sealed class UpdateIntegrationTests
{
    // 真小卷 ENOSPC：docker --tmpfs 8M 挂 /vol，install 置于卷内，
    // backup 暂存与载荷写入都在同卷竞争空间——换包半途必炸 ENOSPC，
    // 断言 install 逐字节回原树、marker/瞬备/残渣全清。
    // 引导件用 linux-musl-x64 入包件——alpine 内可直跑（glibc 件不能）。
    [Fact]
    [Trait("Requires", "docker")]
    public void DockerTmpfsEnospc_HalfSwapRestoresInstall()
    {
        DockerRunner.RequireImage("alpine:latest");
        var root = Path.Combine(Path.GetTempPath(),
            "bundler-enospc-" + Guid.NewGuid().ToString("N")[..10]);
        try
        {
            var work = Path.Combine(root, "work");
            var materialInstall = Path.Combine(work, "material", "install");
            var materialPayload = Path.Combine(work, "material", "payload");
            Directory.CreateDirectory(materialInstall);
            Directory.CreateDirectory(materialPayload);
            File.Copy(Path.Combine(RepositoryLayout.Root,
                    "src/Bundler.Updater.Bootstrap/tools/linux-musl-x64/bundler-updater"),
                Path.Combine(work, "bundler-updater"));
            File.WriteAllText(Path.Combine(materialInstall, "app.txt"), "v1-app");
            // 空间账：vol 8M，install 2M → 瞬备占 2M 后余 ~6M；载荷 7M 写 install 半途炸。
            WriteSized(Path.Combine(materialInstall, "big.bin"), 2 * 1024 * 1024);
            File.WriteAllText(Path.Combine(materialPayload, "app.txt"), "v2-app");
            WriteSized(Path.Combine(materialPayload, "big.bin"), 7 * 1024 * 1024);
            File.WriteAllText(Path.Combine(materialPayload, "new.txt"), "v2-new");

            var result = DockerRunner.RunScript("alpine:latest",
                "set -u;" +
                "cp -a /work/material/install /vol/install;" +
                "cp /work/bundler-updater /tmp/u && chmod +x /tmp/u;" +
                "/tmp/u apply --install-dir /vol/install " +
                "--payload /work/material/payload --log /work/u.log;" +
                "rc=$?; echo \"apply rc=$rc\";" +
                "test \"$rc\" -ne 0 || exit 10;" +
                "cat /vol/install/app.txt | grep -q v1-app || exit 11;" +
                "cmp -s /vol/install/big.bin /work/material/install/big.bin || exit 12;" +
                "test ! -e /vol/install.bundler-swap || exit 13;" +
                "test ! -d /vol/install.bundler-backup || exit 14;" +
                "! ls /vol | grep -q partial- || exit 15;" +
                "! ls /vol/install | grep -q partial- || exit 15;" +
                "echo ENOSPC-OK",
                [new DockerRunner.Mount(work, "/work", ReadOnly: false)],
                extraArgs: "--tmpfs /vol:rw,size=8m");
            Assert.True(result.ExitCode == 0,
                $"docker tmpfs ENOSPC leg failed (rc={result.ExitCode})\n" +
                $"stdout:{result.StdOut}\nstderr:{result.StdErr}\n" +
                "(10=换包未拒 11=v1-app 损坏 12=big.bin 损坏 13=marker 残留 " +
                "14=瞬备残留 15=partial 残渣)");
            Assert.Contains("ENOSPC-OK", result.StdOut);
        }
        finally
        {
            try { Directory.Delete(root, true); } catch { /* best-effort */ }
        }
    }

    // macOS 真小卷：hdiutil 系统自带免 root，8M HFS+ dmg 挂成卷后同账算换包。
    [Fact]
    public void MacHdiutilEnospc_HalfSwapRestoresInstall()
    {
        Assert.SkipUnless(OperatingSystem.IsMacOS(), "hdiutil 小卷腿只在 macOS 宿主跑。");
        var root = Path.Combine(Path.GetTempPath(),
            "bundler-enospc-" + Guid.NewGuid().ToString("N")[..10]);
        var volume = Path.Combine(root, "vol");
        var dmg = Path.Combine(root, "small.dmg");
        try
        {
            Directory.CreateDirectory(volume);
            var work = StageMaterial(root);
            ProcessRunner.AssertSuccess(ProcessRunner.Run("hdiutil",
                ["create", "-size", "8m", "-fs", "HFS+", "-volname", "bundler-enospc", dmg]),
                "hdiutil create failed.");
            ProcessRunner.AssertSuccess(ProcessRunner.Run("hdiutil",
                ["attach", dmg, "-mountpoint", volume, "-nobrowse"]),
                "hdiutil attach failed.");
            var updater = Path.Combine(root, "u");
            File.Copy(Path.Combine(RepositoryLayout.Root,
                    $"src/Bundler.Updater.Bootstrap/tools/osx-{OsxRid()}/bundler-updater"),
                updater, overwrite: true);
            ProcessRunner.AssertSuccess(ProcessRunner.Run("chmod", ["+x", updater]), "chmod failed.");
            var result = ProcessRunner.Run("/bin/sh", ["-c", SwapAssertScript(
                updater, volume, Path.Combine(work, "material"),
                Path.Combine(work, "u.log"))],
                new ProcessRunner.Options { Timeout = TimeSpan.FromMinutes(5) });
            Assert.True(result.ExitCode == 0,
                $"mac hdiutil ENOSPC leg failed (rc={result.ExitCode})\n" +
                $"stdout:{result.StdOut}\nstderr:{result.StdErr}");
            Assert.Contains("ENOSPC-OK", result.StdOut);
        }
        finally
        {
            if (Directory.Exists(volume))
            {
                ProcessRunner.Run("hdiutil", ["detach", volume, "-quiet"],
                    new ProcessRunner.Options { Timeout = TimeSpan.FromMinutes(1) });
            }
            try { Directory.Delete(root, true); } catch { /* best-effort */ }
        }
    }

    // Windows 真小卷：本机 VHD 即可（diskpart 建 8M 虚拟盘→FAT→挂载→分离即删），
    // 非破坏、不需独立 VM——只要求管理员权限。
    [Fact]
    [Trait("Requires", "elevation")]
    public void WinVhdEnospc_HalfSwapRestoresInstall()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows() &&
            System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture ==
                System.Runtime.InteropServices.Architecture.X64,
            "VHD 小卷腿只在 Windows x64 宿主跑。");
        ElevatedRunner.RequireWindowsAdministrator("VHD 挂/卸需要管理员权限。");
        var root = Path.Combine(Path.GetTempPath(),
            "bundler-enospc-" + Guid.NewGuid().ToString("N")[..10]);
        var vhd = Path.Combine(root, "small.vhdx");
        try
        {
            Directory.CreateDirectory(root);
            var work = StageMaterial(root);
            var vhdQ = vhd.Replace("\\", "\\\\");
            var letter = FreeDriveLetter();
            Diskpart(
                $"create vdisk file=\"{vhdQ}\" maximum=8 type=fixed",
                $"select vdisk file=\"{vhdQ}\"",
                "attach vdisk",
                "create partition primary",
                "format fs=fat quick label=enospc",  // 8M 卷 fat32 下限不够，fat 自适应
                $"assign letter={letter}");
            var volume = letter + ":\\";
            var updater = Path.Combine(root, "u.exe");
            File.Copy(Path.Combine(RepositoryLayout.Root,
                    "src/Bundler.Updater.Bootstrap/tools/win-x64/bundler-updater.exe"),
                updater, overwrite: true);
            var install = Path.Combine(volume, "install");
            var payload = Path.Combine(volume, "payload");
            CopyTree(Path.Combine(work, "material", "install"), install);
            CopyTree(Path.Combine(work, "material", "payload"), payload);
            var swap = ProcessRunner.Run(updater,
                ["apply", "--install-dir", install, "--payload", payload,
                 "--log", Path.Combine(root, "u.log")],
                new ProcessRunner.Options { Timeout = TimeSpan.FromMinutes(5) });
            Assert.NotEqual(0, swap.ExitCode);
            Assert.Equal("v1-app", File.ReadAllText(Path.Combine(install, "app.txt")));
            Assert.Equal(File.ReadAllBytes(Path.Combine(work, "material", "install", "big.bin")),
                File.ReadAllBytes(Path.Combine(install, "big.bin")));
            Assert.False(File.Exists(install.TrimEnd('\\', '/') + ".bundler-swap"));
            Assert.False(Directory.Exists(install.TrimEnd('\\', '/') + ".bundler-backup"));
            Assert.Empty(Directory.EnumerateFiles(volume, "*.partial-*", SearchOption.AllDirectories));
        }
        finally
        {
            if (File.Exists(vhd))
            {
                try
                {
                    var vhdQ = vhd.Replace("\\", "\\\\");
                    Diskpart($"select vdisk file=\"{vhdQ}\"", "detach vdisk");
                }
                catch { /* best-effort */ }
            }
            try { Directory.Delete(root, true); } catch { /* best-effort */ }
        }
    }

    static string OsxRid() =>
        System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture ==
            System.Runtime.InteropServices.Architecture.Arm64 ? "osx-arm64" : "osx-x64";

    // 材料：install 2M + 载荷 7M 小文件树（与 docker 腿同账）。
    static string StageMaterial(string root)
    {
        var work = Path.Combine(root, "work");
        var install = Path.Combine(work, "material", "install");
        var payload = Path.Combine(work, "material", "payload");
        Directory.CreateDirectory(install);
        Directory.CreateDirectory(payload);
        File.WriteAllText(Path.Combine(install, "app.txt"), "v1-app");
        WriteSized(Path.Combine(install, "big.bin"), 2 * 1024 * 1024);
        File.WriteAllText(Path.Combine(payload, "app.txt"), "v2-app");
        WriteSized(Path.Combine(payload, "big.bin"), 7 * 1024 * 1024);
        File.WriteAllText(Path.Combine(payload, "new.txt"), "v2-new");
        return work;
    }

    // POSIX 断言脚本：换包被拒后 install 逐字节还原、marker/瞬备/残渣清场。
    static string SwapAssertScript(string updater, string volume, string material,
        string logPath) =>
        "set -u;" +
        $"cp -a {material}/install {volume}/install;" +
        $"{updater} apply --install-dir {volume}/install " +
        $"--payload {material}/payload --log {logPath};" +
        "rc=$?; echo \"apply rc=$rc\";" +
        "test \"$rc\" -ne 0 || exit 10;" +
        $"cat {volume}/install/app.txt | grep -q v1-app || exit 11;" +
        $"cmp -s {volume}/install/big.bin {material}/install/big.bin || exit 12;" +
        $"test ! -e {volume}/install.bundler-swap || exit 13;" +
        $"test ! -d {volume}/install.bundler-backup || exit 14;" +
        $"! ls {volume} | grep -q partial- || exit 15;" +
        $"! ls {volume}/install | grep -q partial- || exit 15;" +
        "echo ENOSPC-OK";

    static void Diskpart(params string[] lines)
    {
        var script = Path.Combine(Path.GetTempPath(),
            "bundler-diskpart-" + Guid.NewGuid().ToString("N")[..10] + ".txt");
        File.WriteAllLines(script, lines);
        try
        {
            ProcessRunner.AssertSuccess(ProcessRunner.Run("diskpart", ["/s", script],
                new ProcessRunner.Options { Timeout = TimeSpan.FromMinutes(3) }),
                "diskpart failed: " + string.Join(" | ", lines));
        }
        finally
        {
            try { File.Delete(script); } catch { /* best-effort */ }
        }
    }

    static char FreeDriveLetter()
    {
        var used = DriveInfo.GetDrives().Select(d => char.ToUpperInvariant(d.Name[0])).ToHashSet();
        for (var c = 'Z'; c >= 'D'; c--)
        {
            if (!used.Contains(c))
            {
                return c;
            }
        }
        Assert.Fail("no free drive letter for test VHD.");
        return '?';
    }

    static void CopyTree(string source, string destination)
    {
        Directory.CreateDirectory(destination);
        foreach (var dir in Directory.EnumerateDirectories(source, "*", SearchOption.AllDirectories))
        {
            Directory.CreateDirectory(Path.Combine(destination, Path.GetRelativePath(source, dir)));
        }
        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            File.Copy(file, Path.Combine(destination, Path.GetRelativePath(source, file)));
        }
    }

    static void WriteSized(string path, int bytes)
    {
        var chunk = Encoding.ASCII.GetBytes(new string('x', 4096));
        using var stream = File.Create(path);
        for (var written = 0; written < bytes; written += chunk.Length)
        {
            stream.Write(chunk, 0, Math.Min(chunk.Length, bytes - written));
        }
    }
}
