using DotNet.Bundler;
using DotNet.Bundler.Signing.Windows;

namespace Bundler.ApiTests;

/// <summary>
/// 签名提供方公开契约：外部命令签名器显式拉起子进程（回环到本测试宿主，
/// 走 Program.cs 的 --external-sign-fixture 入口）。
/// </summary>
public sealed class SigningApiTests : TempBase
{
    [Fact]
    public async Task RunsExternalWindowsSigningProviderSafely()
    {
        var root = Temp("external-signing").FullName;
        var payload = Path.Combine(root, "payload with spaces.exe");
        var log = Path.Combine(root, "signing.log");
        await File.WriteAllTextAsync(payload, "fixture", TestContext.Current.CancellationToken);
        var processPath = Environment.ProcessPath ?? throw new InvalidOperationException("No test process path is available.");
        var arguments = new List<string>();
        if (Path.GetFileNameWithoutExtension(processPath).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
        {
            arguments.Add(System.Reflection.Assembly.GetExecutingAssembly().Location);
        }
        arguments.AddRange(["--external-sign-fixture", "{path}", "{artifactKind}", "{target}", log]);
        var signer = new WindowsExternalCommandSigner(new WindowsExternalCommandSigningOptions
        {
            Command = processPath,
            Arguments = arguments
        });
        await signer.SignAsync(new BundleSigningRequest(
            payload,
            BundleSigningArtifactKind.PayloadExecutable,
            "External signing fixture",
            "windows-x86_64"), TestContext.Current.CancellationToken);
        var record = await File.ReadAllTextAsync(log, TestContext.Current.CancellationToken);
        Assert.Equal($"{payload}|PayloadExecutable|windows-x86_64", record.Trim());

        const string secret = "must-not-leak-provider-secret";
        var failingArguments = arguments.Concat([secret]).ToArray();
        var failingSigner = new WindowsExternalCommandSigner(new WindowsExternalCommandSigningOptions
        {
            Command = processPath,
            Arguments = failingArguments
        });
        var signingFailure = await Assert.ThrowsAnyAsync<InvalidOperationException>(
            () => failingSigner.SignAsync(new BundleSigningRequest(
                payload,
                BundleSigningArtifactKind.Installer,
                "External signing fixture",
                "windows-x86_64"), TestContext.Current.CancellationToken));
        Assert.Contains("exit code 17", signingFailure.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(secret, signingFailure.Message, StringComparison.Ordinal);
    }
}
