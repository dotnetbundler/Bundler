// 集成腿共享宿主系统态（macOS lsregister/收据、Linux 提权装包、MSI 单例、
// 端口/目录资源）——禁止集合间并行，保持串行模型，与 csproj 注释的声明一致。
[assembly: Xunit.v3.Parallelization(Mode = Xunit.Sdk.ParallelMode.None)]
