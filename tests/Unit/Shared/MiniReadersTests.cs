using System.Text;

namespace Bundler.UnitTests.Shared;

// Shared 读器是测试侧独立验证件——本身解析逻辑（长名/特殊成员）必须有自测，否则"独立"无意义。
public sealed class MiniReadersTests
{
    [Fact]
    public void MiniAr_GnuLongNameTable_ResolvesSlashOffsetMember()
    {
        var table = Encoding.ASCII.GetBytes("very-long-member-name.bin/\n");
        var ar = new List<byte>();
        ar.AddRange("!<arch>\n"u8.ToArray());
        ar.AddRange(ArMember("//", table));
        ar.AddRange(ArMember("/0", Encoding.ASCII.GetBytes("data")));
        var members = MiniAr.Read(ar.ToArray());
        var member = Assert.Single(members);
        Assert.Equal("very-long-member-name.bin", member.Name);
        Assert.Equal("data", Encoding.ASCII.GetString(member.Data));
    }

    [Fact]
    public void MiniAr_BsdLongName_ReadsNamePrefix()
    {
        var inner = Encoding.ASCII.GetBytes("long-name.txt");
        var data = inner.Concat("X"u8.ToArray()).ToArray();
        var ar = new List<byte>();
        ar.AddRange("!<arch>\n"u8.ToArray());
        ar.AddRange(ArMember($"#1/{inner.Length}", data));
        var member = Assert.Single(MiniAr.Read(ar.ToArray()));
        Assert.Equal("long-name.txt", member.Name);
        Assert.Equal("X", Encoding.ASCII.GetString(member.Data));
    }

    [Fact]
    public void MiniAr_SymbolTable_Skipped()
    {
        var ar = new List<byte>();
        ar.AddRange("!<arch>\n"u8.ToArray());
        ar.AddRange(ArMember("__.SYMDEF", new byte[16]));
        ar.AddRange(ArMember("debian-binary", "2.0\n"u8.ToArray()));
        var member = Assert.Single(MiniAr.Read(ar.ToArray()));
        Assert.Equal("debian-binary", member.Name);
    }

    [Fact]
    public void MiniTar_UstarPrefix_CombinesLongName()
    {
        var dir = new string('d', 60);
        var leaf = new string('f', 50);
        var tar = new List<byte>();
        tar.AddRange(TarHeader(leaf, dir, dataSize: 1));
        tar.AddRange(new byte[512]); // 数据块
        tar.AddRange(new byte[1024]); // 两块零尾
        var entry = Assert.Single(MiniTar.Read(tar.ToArray()));
        Assert.Equal($"{dir}/{leaf}", entry.Name);
    }

    [Fact]
    public void MiniTar_GnuLongNameMarker_OverridesHeaderName()
    {
        var longName = Encoding.UTF8.GetBytes("the-real/very-long-name.txt\0");
        var tar = new List<byte>();
        tar.AddRange(TarHeader("././@LongLink", "", longName.Length, type: (byte)'L'));
        tar.AddRange(longName);
        tar.AddRange(new byte[512 - longName.Length]); // 补齐数据块
        tar.AddRange(TarHeader("stub", "", 3));
        tar.AddRange(Encoding.ASCII.GetBytes("abc").Concat(new byte[509]).ToArray());
        tar.AddRange(new byte[1024]);
        var entry = Assert.Single(MiniTar.Read(tar.ToArray()));
        Assert.Equal("the-real/very-long-name.txt", entry.Name);
        Assert.Equal("abc", Encoding.ASCII.GetString(entry.Data));
    }

    [Fact]
    public void MiniTar_PaxPath_OverridesHeaderName()
    {
        // pax 记录 "<len> path=<value>\n"——len 含自身数字位。
        var pax = Encoding.UTF8.GetBytes("26 path=the-real-name.txt\n");
        var tar = new List<byte>();
        tar.AddRange(TarHeader("./PaxHeaders.1/x", "", pax.Length, type: (byte)'x'));
        tar.AddRange(pax);
        tar.AddRange(new byte[512 - pax.Length]);
        tar.AddRange(TarHeader("stub", "", 2));
        tar.AddRange(Encoding.ASCII.GetBytes("hi").Concat(new byte[510]).ToArray());
        tar.AddRange(new byte[1024]);
        var entry = Assert.Single(MiniTar.Read(tar.ToArray()));
        Assert.Equal("the-real-name.txt", entry.Name);
        Assert.Equal("hi", Encoding.ASCII.GetString(entry.Data));
    }

    [Fact]
    public void MiniTar_PaxWithoutPath_FallsBackToHeaderName()
    {
        // pax 记录无 path 键（如 apk 的 hash 记录）——回退到 x 头自身名，后继条目沿用该名。
        var pax = Encoding.UTF8.GetBytes("21 comment=some-hash\n");
        var tar = new List<byte>();
        tar.AddRange(TarHeader("data.tar.gz", "", pax.Length, type: (byte)'x'));
        tar.AddRange(pax);
        tar.AddRange(new byte[512 - pax.Length]);
        tar.AddRange(TarHeader("data.tar.gz", "", 2));
        tar.AddRange(Encoding.ASCII.GetBytes("hi").Concat(new byte[510]).ToArray());
        tar.AddRange(new byte[1024]);
        var entry = Assert.Single(MiniTar.Read(tar.ToArray()));
        Assert.Equal("data.tar.gz", entry.Name);
    }

    private static byte[] ArMember(string name, byte[] data)
    {
        var h = new byte[60];
        Encoding.ASCII.GetBytes("                ").CopyTo(h, 0);
        Encoding.ASCII.GetBytes(name).CopyTo(h, 0);
        Encoding.ASCII.GetBytes($"{0,-12}{0,-6}{0,-6}100644  {data.Length,-10}`\n").CopyTo(h, 16);
        var body = h.Concat(data).ToList();
        if (data.Length % 2 == 1)
        {
            body.Add(0);
        }
        return body.ToArray();
    }

    private static byte[] TarHeader(string name, string prefix, int dataSize, byte type = (byte)'0')
    {
        var h = new byte[512];
        Encoding.ASCII.GetBytes(name).CopyTo(h, 0);
        Encoding.ASCII.GetBytes("0000644\0").CopyTo(h, 100);
        Encoding.ASCII.GetBytes("0000000\00000000").CopyTo(h, 108);
        Encoding.ASCII.GetBytes(Convert.ToString(dataSize, 8).PadLeft(11, '0') + "\0").CopyTo(h, 124);
        Encoding.ASCII.GetBytes("00000000000\0").CopyTo(h, 136);
        Encoding.ASCII.GetBytes("        ").CopyTo(h, 148); // checksum 空格计算
        h[156] = type;
        Encoding.ASCII.GetBytes("ustar\000").CopyTo(h, 257);
        Encoding.ASCII.GetBytes(prefix).CopyTo(h, 345);
        var sum = h.Sum(b => (int)b);
        Encoding.ASCII.GetBytes(Convert.ToString(sum, 8).PadLeft(6, '0') + "\0 ").CopyTo(h, 148);
        return h;
    }
}
