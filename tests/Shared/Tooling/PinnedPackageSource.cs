// 本地包消费腿的还原源钉死：为每次还原生成一份 NuGet.Config，
// 经 packageSourceMapping 把 DotNet.Bundler.* 只解析到本地源、其余包只解析到 nuget.org——
// 将来公网发布同名包之后多源还原也不会静默选错件（缺包即 NU110x，而非回退别的源）。
internal static class PinnedPackageSource
{
    // 在 directory 下写出 NuGet.Config 并返回路径；调用方把它传给
    // dotnet restore --configfile（该参数替换整个配置链，机器/用户级源不参与）。
    public static string WriteConfig(string directory, string packageDirectory)
    {
        Directory.CreateDirectory(directory);
        var feed = System.Security.SecurityElement.Escape(
            Path.GetFullPath(packageDirectory).Replace('\\', '/'))!;
        var configPath = Path.Combine(directory, "NuGet.Config");
        File.WriteAllText(configPath, $"""
            <?xml version="1.0" encoding="utf-8"?>
            <configuration>
              <packageSources>
                <clear />
                <add key="bundler-local" value="{feed}" />
                <add key="nuget.org" value="https://api.nuget.org/v3/index.json" />
              </packageSources>
              <packageSourceMapping>
                <packageSource key="bundler-local">
                  <package pattern="DotNet.Bundler" />
                  <package pattern="DotNet.Bundler.*" />
                </packageSource>
                <packageSource key="nuget.org">
                  <package pattern="*" />
                </packageSource>
              </packageSourceMapping>
            </configuration>
            """);
        return configPath;
    }
}
