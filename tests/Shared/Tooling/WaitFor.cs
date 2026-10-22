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
}
