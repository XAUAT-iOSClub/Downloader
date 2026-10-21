using System.Text;

namespace Downloader.Migrator;

public static class Program
{
    public static async Task<int> Main(string[] args)
    {
        try
        {
            Console.OutputEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
        }
        catch
        {
            // 某些重定向终端不支持设置编码，忽略。
        }

        CommandLineOptions cli;
        try
        {
            cli = CommandLineOptions.Parse(args);
        }
        catch (CommandLineException exception)
        {
            Console.Error.WriteLine($"参数错误：{exception.Message}");
            Console.Error.WriteLine("使用 --help 查看用法。");
            return 2;
        }

        if (cli.ShowHelp)
        {
            CommandLineOptions.PrintHelp();
            return 0;
        }

        MigrationConfig config;
        try
        {
            config = MigrationConfigLoader.Load(cli);
        }
        catch (ConfigurationException exception)
        {
            Console.Error.WriteLine("配置错误：");
            Console.Error.WriteLine(exception.Message);
            Console.Error.WriteLine();
            Console.Error.WriteLine("参考 Downloader.Migrator/.env.migrate.example 创建配置文件，");
            Console.Error.WriteLine("或用 --config 指定路径。");
            return 2;
        }

        using var cancellationTokenSource = new CancellationTokenSource();
        Console.CancelKeyPress += (_, eventArgs) =>
        {
            eventArgs.Cancel = true;
            cancellationTokenSource.Cancel();
        };

        using var reporter = new Reporter(config.Behavior.LogFile, cli.Verbose);

        try
        {
            var runner = new MigrationRunner(config, cli, reporter);
            var exitCode = await runner.RunAsync(cancellationTokenSource.Token);
            reporter.Info($"退出码：{exitCode}");
            return exitCode;
        }
        catch (OperationCanceledException)
        {
            reporter.Warn("已取消。已复制的对象和已更新的数据库记录会保留；重新运行可继续。");
            return 130;
        }
        catch (Exception exception)
        {
            reporter.Error(exception.ToString());
            return 1;
        }
    }
}
