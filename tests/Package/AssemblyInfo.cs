// 真装腿共享安装状态与 msiexec 单例——禁止集合间并行，保持串行模型，
// 与 csproj 注释的声明一致。
[assembly: Xunit.v3.Parallelization(Mode = Xunit.Sdk.ParallelMode.None)]
