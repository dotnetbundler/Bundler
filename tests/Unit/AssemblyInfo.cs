// Unit 层含 cwd/进程环境变量相关用例（init/discovery/DOTNET_BUNDLER_VERBOSE）：
// 关并行保证进程全局态不被并发用例互踩。全套 <1s，无并行收益。
[assembly: Xunit.v3.Parallelization(Mode = Xunit.Sdk.ParallelMode.None)]
