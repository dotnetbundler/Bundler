global using Xunit;
// 测试沿用原控制台 harness 的串行模型（用例间共享临时目录布局，并行会互删产物）
[assembly: Xunit.v3.Parallelization(Mode = Xunit.Sdk.ParallelMode.None)]
