using System.Text;
using DotNet.Bundler.MacApp;

namespace Bundler.UnitTests.MacApp;

/// <summary>macOS 纯托管内件：plist 读写、Mach-O/FAT 解析与合成、icns 装配、PE 探测。</summary>
public sealed class MacInternalsTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "mac-" + Guid.NewGuid().ToString("N"));

    public MacInternalsTests()
    {
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    // ---- InfoPlist ----

    [Fact]
    public void InfoPlist_WriteThenRead_RoundTripsValues()
    {
        var path = Path.Combine(_root, "Info.plist");
        InfoPlist.Write(path, new Dictionary<string, object>
        {
            ["CFBundleName"] = "Sample",
            ["CFBundleVersion"] = "1.2.3",
            ["LSMinimumSystemVersion"] = "13.0",
            ["NSHighResolutionCapable"] = true,
            ["CFBundleDocumentTypes"] = new List<object>
            {
                new Dictionary<string, object> { ["CFBundleTypeName"] = "Doc" }
            },
        });

        var values = InfoPlist.ReadDictionary(path);
        Assert.Equal("Sample", values["CFBundleName"]);
        Assert.Equal(true, values["NSHighResolutionCapable"]);
        Assert.IsType<List<object>>(values["CFBundleDocumentTypes"]);

        var strings = InfoPlist.ReadStringValues(path);
        Assert.Equal("1.2.3", strings["CFBundleVersion"]);
    }

    [Fact]
    public void InfoPlist_ParseXml_RejectsForeignDtd()
    {
        // plist 无 DTD/外部实体——畸形文档必须拒。
        Assert.ThrowsAny<Exception>(() => InfoPlist.ParseXml("<plist version=\"1.0\"><broken", "test"));
        var parsed = InfoPlist.ParseXml("""
            <plist version="1.0"><dict><key>K</key><string>V</string></dict></plist>
            """, "test");
        Assert.Equal("V", parsed["K"]);
    }

    // ---- MachO 头解析（手造魔数） ----

    private string WriteThinMachO(uint cpuTypeLe, bool le64 = true)
    {
        var path = Path.Combine(_root, "thin-" + Guid.NewGuid().ToString("N")[..6] + ".bin");
        var bytes = new byte[32];
        // MH_CIGAM_64：盘上小端魔数 0xCFFAEDFE → 大端读得该值。
        bytes[0] = 0xCF; bytes[1] = 0xFA; bytes[2] = 0xED; bytes[3] = 0xFE;
        // cputype @4 小端。
        bytes[4] = (byte)cpuTypeLe;
        bytes[5] = (byte)(cpuTypeLe >> 8);
        bytes[6] = (byte)(cpuTypeLe >> 16);
        bytes[7] = (byte)(cpuTypeLe >> 24);
        File.WriteAllBytes(path, bytes);
        return path;
    }

    [Fact]
    public void MachO_ThinHeader_ReadsArchitecture()
    {
        Assert.Equal(["x86_64"], MachO.ReadArchitectures(WriteThinMachO(0x01000007)));
        Assert.Equal(["arm64"], MachO.ReadArchitectures(WriteThinMachO(0x0100000C)));
    }

    [Fact]
    public void MachO_NonMachO_EmptyList()
    {
        var path = Path.Combine(_root, "not.bin");
        File.WriteAllBytes(path, [0x00, 0x01, 0x02, 0x03, 0x04, 0x05, 0x06, 0x07, 0x08]);
        Assert.Empty(MachO.ReadArchitectures(path));
        Assert.False(MachO.IsMachO(path));
        Assert.Empty(MachO.ReadSliceInfos(path));
    }

    [Fact]
    public void MachO_TruncatedFile_EmptyList()
    {
        var path = Path.Combine(_root, "tiny.bin");
        File.WriteAllBytes(path, [0xCF, 0xFA]);
        Assert.Empty(MachO.ReadArchitectures(path));
    }

    // ---- MachOFat.Create → MachO.ReadSliceInfos 往返 ----

    [Fact]
    public void MachOFat_Create_ThenReadSlices()
    {
        var x64 = Encoding.ASCII.GetBytes("x86_64-code-bytes");
        var arm = Encoding.ASCII.GetBytes("arm64-code-bytes!");
        var output = Path.Combine(_root, "fat.bin");

        MachOFat.Create(output,
        [
            new FatSlice(0x01000007, 3, x64.Length, s => s.Write(x64)),
            new FatSlice(0x0100000C, 0, arm.Length, s => s.Write(arm)),
        ]);

        Assert.Equal(["x86_64", "arm64"], MachO.ReadArchitectures(output));
        var slices = MachO.ReadSliceInfos(output);
        Assert.Equal(2, slices.Count);
        foreach (var (slice, expected) in slices.Zip(new[] { x64, arm }))
        {
            var content = new byte[slice.Size];
            using (var stream = File.OpenRead(output))
            {
                stream.Position = slice.Offset;
                stream.ReadExactly(content);
            }
            Assert.Equal(expected, content);
        }
    }

    // ---- IcnsBuilder ----

    private static byte[] Png(int width, int height)
    {
        var png = new byte[24];
        new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }.CopyTo(png, 0);
        png[12] = (byte)'I'; png[13] = (byte)'H'; png[14] = (byte)'D'; png[15] = (byte)'R';
        png[16] = (byte)(width >> 24); png[17] = (byte)(width >> 16);
        png[18] = (byte)(width >> 8); png[19] = (byte)width;
        png[20] = (byte)(height >> 24); png[21] = (byte)(height >> 16);
        png[22] = (byte)(height >> 8); png[23] = (byte)height;
        return png;
    }

    [Fact]
    public void Icns_Build_AssemblesMagicAndChunks()
    {
        var icns = IcnsBuilder.BuildFromPngs([Png(32, 32), Png(128, 128)]);
        Assert.Equal("icns", Encoding.ASCII.GetString(icns, 0, 4));
        var total = (icns[4] << 24) | (icns[5] << 16) | (icns[6] << 8) | icns[7];
        Assert.Equal(icns.Length, total);
        Assert.Equal("ic11", Encoding.ASCII.GetString(icns, 8, 4)); // 32px → ic11
        Assert.Equal("ic07", Encoding.ASCII.GetString(icns, 8 + 8 + 24, 4)); // 128px → ic07
    }

    [Fact]
    public void Icns_Build_RejectsBadInputs()
    {
        Assert.Throws<ArgumentException>(() => IcnsBuilder.BuildFromPngs([]));
        Assert.Throws<InvalidDataException>(() =>
            IcnsBuilder.BuildFromPngs([Png(32, 64)]));              // 非方
        Assert.Throws<InvalidDataException>(() =>
            IcnsBuilder.BuildFromPngs([Png(48, 48)]));              // 非支持档
        Assert.Throws<InvalidDataException>(() =>
            IcnsBuilder.BuildFromPngs([Png(32, 32), Png(32, 32)])); // 重复档
        Assert.Throws<InvalidDataException>(() =>
            IcnsBuilder.BuildFromPngs([[1, 2, 3]]));                // 非 PNG
    }

    // ---- PortableExecutable ----

    [Fact]
    public void PE_ManagedAssembly_CliDirectorySet()
    {
        var path = Path.Combine(_root, "managed.dll");
        var bytes = new byte[0x200];
        bytes[0] = (byte)'M'; bytes[1] = (byte)'Z';
        const int pe = 0x80;
        bytes[0x3C] = pe; // lfanew 低字节
        bytes[pe] = (byte)'P'; bytes[pe + 1] = (byte)'E';
        // PE32 magic @ optional header。
        bytes[pe + 24] = 0x0B; bytes[pe + 25] = 0x01;
        // CLI 数据目录 = dirBase + 14*8；PE32 dirBase = pe+24+96。
        var cli = pe + 24 + 96 + 14 * 8;
        bytes[cli] = 0x10; bytes[cli + 4] = 0x20; // RVA+Size 非零
        File.WriteAllBytes(path, bytes);
        Assert.True(PortableExecutable.IsManagedAssembly(path));
    }

    [Fact]
    public void PE_NativeAssembly_CliDirectoryZeroed()
    {
        var path = Path.Combine(_root, "native.exe");
        var bytes = new byte[0x200];
        bytes[0] = (byte)'M'; bytes[1] = (byte)'Z';
        const int pe = 0x80;
        bytes[0x3C] = pe;
        bytes[pe] = (byte)'P'; bytes[pe + 1] = (byte)'E';
        bytes[pe + 24] = 0x0B; bytes[pe + 25] = 0x01; // PE32，CLI 目录全零
        File.WriteAllBytes(path, bytes);
        Assert.False(PortableExecutable.IsManagedAssembly(path));
    }

    [Fact]
    public void PE_NonPe_False()
    {
        var path = Path.Combine(_root, "elf.bin");
        File.WriteAllBytes(path, [0x7F, (byte)'E', (byte)'L', (byte)'F']);
        Assert.False(PortableExecutable.IsManagedAssembly(path));
    }

    // ---- MacUniversalPayloadMerger ----

    [Fact]
    public void UniversalMerge_IdenticalNonMachO_Merged()
    {
        var a = Dir("a", ("shared.txt", "same-bytes"));
        var b = Dir("b", ("shared.txt", "same-bytes"));
        var output = Path.Combine(_root, "merged");

        MacUniversalPayloadMerger.Merge([a, b], output);

        Assert.Equal("same-bytes", File.ReadAllText(Path.Combine(output, "shared.txt")));
    }

    [Fact]
    public void UniversalMerge_OneSidedFile_Copied()
    {
        var a = Dir("a2", ("only-a.txt", "a"));
        var b = Dir("b2", ("only-b.txt", "b"));
        var output = Path.Combine(_root, "merged2");

        MacUniversalPayloadMerger.Merge([a, b], output);

        Assert.Equal("a", File.ReadAllText(Path.Combine(output, "only-a.txt")));
        Assert.Equal("b", File.ReadAllText(Path.Combine(output, "only-b.txt")));
    }

    [Fact]
    public void UniversalMerge_ThinMachOs_Fattened()
    {
        var a = Dir("a3");
        var b = Dir("b3");
        File.WriteAllBytes(Path.Combine(a, "lib.dylib"), ThinMachOBytes(0x01000007));
        File.WriteAllBytes(Path.Combine(b, "lib.dylib"), ThinMachOBytes(0x0100000C));
        var output = Path.Combine(_root, "merged3");

        MacUniversalPayloadMerger.Merge([a, b], output);

        Assert.Equal(["x86_64", "arm64"],
            MachO.ReadArchitectures(Path.Combine(output, "lib.dylib")));
    }

    [Fact]
    public void UniversalMerge_SingleSidedThinMachO_Rejected()
    {
        var a = Dir("a4");
        var b = Dir("b4", ("note.txt", "n"));
        File.WriteAllBytes(Path.Combine(a, "solo.dylib"), ThinMachOBytes(0x01000007));
        var output = Path.Combine(_root, "merged4");

        Assert.Throws<InvalidDataException>(() => MacUniversalPayloadMerger.Merge([a, b], output));
    }

    [Fact]
    public void UniversalMerge_ConflictingOpaqueFiles_Rejected()
    {
        var a = Dir("a5", ("conf.dat", "aaa"));
        var b = Dir("b5", ("conf.dat", "bbb"));
        var output = Path.Combine(_root, "merged5");

        Assert.Throws<InvalidDataException>(() => MacUniversalPayloadMerger.Merge([a, b], output));
    }

    [Fact]
    public void UniversalMerge_ReferenceSuppliesNeutralCopy()
    {
        var a = Dir("a6", ("conf.dat", "aaa"));
        var b = Dir("b6", ("conf.dat", "bbb"));
        var reference = Dir("ref6", ("conf.dat", "neutral"));
        var output = Path.Combine(_root, "merged6");

        MacUniversalPayloadMerger.Merge([a, b], output, reference);

        Assert.Equal("neutral", File.ReadAllText(Path.Combine(output, "conf.dat")));
    }

    [Fact]
    public void UniversalMerge_RejectsBadInputs()
    {
        var a = Dir("a7");
        Assert.Throws<ArgumentException>(() =>
            MacUniversalPayloadMerger.Merge([a], Path.Combine(_root, "m")));
        Assert.Throws<DirectoryNotFoundException>(() =>
            MacUniversalPayloadMerger.Merge([a, Path.Combine(_root, "ghost")], Path.Combine(_root, "m2")));
        var nonEmpty = Dir("nonempty", ("x", "y"));
        Assert.Throws<IOException>(() =>
            MacUniversalPayloadMerger.Merge([a, Dir("b7")], nonEmpty));
    }

    private static byte[] ThinMachOBytes(uint cpuType)
    {
        var bytes = new byte[32];
        bytes[0] = 0xCF; bytes[1] = 0xFA; bytes[2] = 0xED; bytes[3] = 0xFE;
        bytes[4] = (byte)cpuType;
        bytes[5] = (byte)(cpuType >> 8);
        bytes[6] = (byte)(cpuType >> 16);
        bytes[7] = (byte)(cpuType >> 24);
        return bytes;
    }

    private string Dir(string name, params (string File, string Content)[] files)
    {
        var path = Path.Combine(_root, name);
        Directory.CreateDirectory(path);
        foreach (var (file, content) in files)
        {
            File.WriteAllText(Path.Combine(path, file), content);
        }
        return path;
    }
}
