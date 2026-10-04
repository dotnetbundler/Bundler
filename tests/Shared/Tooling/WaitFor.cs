// 轮询等待：安装器异步清理、LaunchServices 注册、hook 标记出现等场景的等待原语。
internal static class WaitFor
{
    public static void Until(Func<bool> condition, string message, int timeoutSeconds = 15)
    {
        var deadline = DateTime.UtcNow.AddSeconds(timeoutSeconds);
        while (DateTime.UtcNow < deadline)
        {
            if (condition())
            {
                return;
            }
            Thread.Sleep(200);
        }
        Assert.Fail(message);
    }

    // 限定窗口内"不得发生"：用于 open -W 被隔离/最低系统版本拒绝等"应保持不成立"腿。
    // 返回 true 表示条件在窗口内一直未成立（即操作被正确拦下）。
    public static bool RemainsFalse(Func<bool> condition, int windowSeconds)
    {
        var deadline = DateTime.UtcNow.AddSeconds(windowSeconds);
        while (DateTime.UtcNow < deadline)
        {
            if (condition())
            {
                return false;
            }
            Thread.Sleep(500);
        }
        return true;
    }
}
