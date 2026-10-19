using System.Diagnostics;

// Api/Integration 双侧链接。Api 只走公开面、不引 Bundler.Core internals——
// 与产品侧 DotNet.Bundler.Core.ProcessWait 同实现的测试件拷贝。
/// <summary>
/// 有界进程退出等待，替代 <see cref="Process.WaitForExit(int)"/>——CI 实证过
/// macOS 上子进程成 zombie 而 WaitForExit 永不返回的丢唤醒面；逐趟 HasExited
/// 直探内核退出态，与运行时事件链解耦。
/// </summary>
internal static class TestProcessExit
{
    public static bool WaitForExit(Process process, int milliseconds)
    {
        var elapsed = Stopwatch.StartNew();
        while (!process.HasExited)
        {
            if (milliseconds >= 0 && elapsed.ElapsedMilliseconds >= milliseconds)
            {
                return false;
            }
            var poll = elapsed.ElapsedMilliseconds < 2_000 ? 5 : 100;
            Thread.Sleep(milliseconds < 0
                ? poll
                : (int)Math.Max(1, Math.Min(poll, milliseconds - elapsed.ElapsedMilliseconds)));
        }
        return true;
    }
}
