// 各 API fixture 的输出根目录契约：集成脚本经环境变量指到其可控目录以回读产物；
// 未设置时用独立临时目录（dotnet test 直接跑也不冲突）
internal static class ApiFixtureRoot
{
    public static string For(string environmentVariable, string fixtureName)
    {
        return Environment.GetEnvironmentVariable(environmentVariable) ??
            Path.Combine(Path.GetTempPath(), "DotNet.Bundler.ApiTests", fixtureName, Guid.NewGuid().ToString("N"));
    }

    // 写一份最小可过校验的假 publish 输入
    public static string WriteShellPayload(string root)
    {
        var input = Path.Combine(root, "input");
        Directory.CreateDirectory(input);
        File.WriteAllText(Path.Combine(input, "FixtureApp"), "#!/bin/sh\necho fixture\n");
        File.WriteAllText(Path.Combine(input, "FixtureApp.dll"), "payload-bytes");
        return input;
    }

    // .app/.dmg/.pkg 假载荷只需 Mach-O 魔数+cputype 过校验——cputype 随宿主架构取
    // （ApiTests 的 Target 是 OsxTarget 宿主推导；ARM64=0x0100000C、X86_64=0x01000007 小端）
    public static async Task<string> WriteMachOPayloadAsync(string root)
    {
        var input = Path.Combine(root, "publish");
        Directory.CreateDirectory(input);
        var executable = new byte[64];
        executable[0] = 0xCF; executable[1] = 0xFA; executable[2] = 0xED; executable[3] = 0xFE;
        executable[4] = System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture ==
            System.Runtime.InteropServices.Architecture.Arm64 ? (byte)0x0C : (byte)0x07;
        executable[5] = 0x00; executable[6] = 0x00; executable[7] = 0x01;
        await File.WriteAllBytesAsync(Path.Combine(input, "ApiFixture"), executable, TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(Path.Combine(input, "ApiFixture.dll"), "package-api-fixture", TestContext.Current.CancellationToken);
        return input;
    }
}
