namespace DotNet.Bundler.Updater.Bootstrap;

public static class Program
{
    // 退出码：0 成功；2 用法错误；3 等待宿主退出超时；4 备份/换包失败（已尽力回滚）。
    public static int Main(string[] args)
    {
        try
        {
            return BootstrapPlan.Run(args);
        }
        catch (UsageException exception)
        {
            Console.Error.WriteLine($"bundler-updater: {exception.Message}");
            PrintUsage(Console.Error);
            return 2;
        }
        catch (WaitTimeoutException)
        {
            Console.Error.WriteLine("bundler-updater: the target process did not exit in time.");
            return 3;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"bundler-updater: {exception.Message}");
            return 4;
        }
    }

    private static void PrintUsage(TextWriter writer)
    {
        writer.WriteLine("Usage:");
        writer.WriteLine("  bundler-updater apply --install-dir <dir> --payload <dir>");
        writer.WriteLine("      [--wait-pid <pid>] [--app <path>] [--backup-dir <dir>]");
        writer.WriteLine("      [--keep-payload] [--log <file>] [--wait-timeout <seconds>]");
        writer.WriteLine("  bundler-updater apply --install-dir <dir> --rollback");
    }
}
