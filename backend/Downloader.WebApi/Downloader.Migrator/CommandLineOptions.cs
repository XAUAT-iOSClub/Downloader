namespace Downloader.Migrator;

public enum MigrationScope
{
    All,
    Soft,
    Icons
}

public enum VerifyMode
{
    None,
    Size,
    Sha256
}

public sealed class CommandLineOptions
{
    public bool Apply { get; private set; }
    public bool CopyEnabled { get; private set; } = true;
    public bool UpdateDatabase { get; private set; } = true;
    public bool DeleteSource { get; private set; }
    public bool Force { get; private set; }
    public bool Verbose { get; private set; }
    public bool ShowHelp { get; private set; }
    public bool IgnoreUnresolved { get; private set; }

    public MigrationScope Scope { get; private set; } = MigrationScope.All;
    public string? AppId { get; private set; }
    public int? Limit { get; private set; }
    public int? Concurrency { get; private set; }
    public int? PartSizeMb { get; private set; }
    public VerifyMode? Verify { get; private set; }

    public string? ConfigPath { get; private set; }
    public string? StateFile { get; private set; }
    public string? ReportFile { get; private set; }
    public string? LogFile { get; private set; }

    public static CommandLineOptions Parse(string[] args)
    {
        var options = new CommandLineOptions();

        for (var i = 0; i < args.Length; i++)
        {
            var argument = args[i];
            if (string.IsNullOrWhiteSpace(argument))
            {
                continue;
            }

            var (name, inlineValue) = SplitArgument(argument);
            string? ReadValue()
            {
                if (inlineValue != null)
                {
                    return inlineValue;
                }

                if (i + 1 >= args.Length || args[i + 1].StartsWith('-'))
                {
                    throw new CommandLineException($"参数 {name} 需要一个值。");
                }

                return args[++i];
            }

            switch (name.ToLowerInvariant())
            {
                case "--apply":
                    EnsureNoValue(name, inlineValue);
                    options.Apply = true;
                    break;
                case "--dry-run":
                    EnsureNoValue(name, inlineValue);
                    options.Apply = false;
                    break;
                case "--no-copy":
                    EnsureNoValue(name, inlineValue);
                    options.CopyEnabled = false;
                    break;
                case "--no-db":
                    EnsureNoValue(name, inlineValue);
                    options.UpdateDatabase = false;
                    break;
                case "--delete-source":
                    EnsureNoValue(name, inlineValue);
                    options.DeleteSource = true;
                    break;
                case "--force":
                    EnsureNoValue(name, inlineValue);
                    options.Force = true;
                    break;
                case "--verbose":
                case "-v":
                    EnsureNoValue(name, inlineValue);
                    options.Verbose = true;
                    break;
                case "--ignore-unresolved":
                    EnsureNoValue(name, inlineValue);
                    options.IgnoreUnresolved = true;
                    break;
                case "--help":
                case "-h":
                    options.ShowHelp = true;
                    break;
                case "--only":
                    options.Scope = ParseScope(ReadValue()!);
                    break;
                case "--app-id":
                    options.AppId = ReadValue();
                    break;
                case "--limit":
                    options.Limit = ParsePositiveInt(name, ReadValue());
                    break;
                case "--concurrency":
                    options.Concurrency = ParsePositiveInt(name, ReadValue());
                    if (options.Concurrency is < 1 or > 32)
                    {
                        throw new CommandLineException("--concurrency 必须在 1 到 32 之间。");
                    }

                    break;
                case "--part-size-mb":
                    options.PartSizeMb = ParsePositiveInt(name, ReadValue());
                    if (options.PartSizeMb is < 5 or > 512)
                    {
                        throw new CommandLineException("--part-size-mb 必须在 5 到 512 之间。");
                    }

                    break;
                case "--verify":
                    options.Verify = ParseVerify(ReadValue()!);
                    break;
                case "--config":
                    options.ConfigPath = ReadValue();
                    break;
                case "--state-file":
                    options.StateFile = ReadValue();
                    break;
                case "--report":
                    options.ReportFile = ReadValue();
                    break;
                case "--log-file":
                    options.LogFile = ReadValue();
                    break;
                default:
                    throw new CommandLineException($"未知参数：{argument}。使用 --help 查看用法。");
            }
        }

        if (!options.CopyEnabled && !options.UpdateDatabase)
        {
            throw new CommandLineException("--no-copy 和 --no-db 不能同时使用。");
        }

        if (options.DeleteSource && !options.Apply)
        {
            throw new CommandLineException("--delete-source 必须与 --apply 一起使用。");
        }

        if (options.DeleteSource && !options.CopyEnabled)
        {
            throw new CommandLineException("--delete-source 不能与 --no-copy 一起使用。");
        }

        return options;
    }

    private static (string Name, string? Value) SplitArgument(string argument)
    {
        var separatorIndex = argument.IndexOf('=');
        if (separatorIndex <= 0)
        {
            return (argument, null);
        }

        return (argument[..separatorIndex], argument[(separatorIndex + 1)..]);
    }

    private static void EnsureNoValue(string name, string? value)
    {
        if (value != null)
        {
            throw new CommandLineException($"参数 {name} 不接受值。");
        }
    }

    private static int ParsePositiveInt(string name, string? value)
    {
        if (!int.TryParse(value, out var result) || result <= 0)
        {
            throw new CommandLineException($"{name} 需要一个正整数。");
        }

        return result;
    }

    private static MigrationScope ParseScope(string value)
    {
        return value.ToLowerInvariant() switch
        {
            "all" => MigrationScope.All,
            "soft" or "softs" => MigrationScope.Soft,
            "icon" or "icons" or "app" or "apps" => MigrationScope.Icons,
            _ => throw new CommandLineException($"--only 只支持 all、soft、icons，收到：{value}。")
        };
    }

    private static VerifyMode ParseVerify(string value)
    {
        return value.ToLowerInvariant() switch
        {
            "none" or "off" => VerifyMode.None,
            "size" => VerifyMode.Size,
            "sha256" or "sha" => VerifyMode.Sha256,
            _ => throw new CommandLineException($"--verify 只支持 none、size、sha256，收到：{value}。")
        };
    }

    public static void PrintHelp()
    {
        Console.WriteLine("""
            Downloader 存储迁移工具：Cloudflare R2 -> 腾讯云 COS

            用法：
              dotnet run --project Downloader.Migrator -- [选项]

            默认是 dry-run：只读取数据库和两个存储端做检查，不复制、不写库。
            确认计划无误后加 --apply 真正执行。

            核心选项：
              --apply                 执行复制和数据库更新（默认只做 dry-run）
              --config <path>         指定配置文件（默认向上查找 .env.migrate）
              --only <all|soft|icons> 只迁移安装包或只迁移应用图标（默认 all）
              --app-id <id>           只处理某个 App 下的记录
              --limit <n>             最多处理 n 个存储对象
              --concurrency <n>       并发对象数，1-32（默认 4）
              --verify <size|sha256|none>
                                      复制后的校验方式（默认 size）
              --force                 目标对象已存在但大小不同时覆盖它
              --no-db                 只复制对象，不更新数据库
              --no-copy               不复制对象，只校验目标后更新数据库
              --delete-source         校验通过后删除 R2 源对象（需 --apply，默认关闭）
              --ignore-unresolved     忽略无法识别的 URL，不因此返回失败
              --state-file <path>     断点/审计状态文件路径
              --report <path>         CSV 报告路径
              --log-file <path>       日志文件路径
              --verbose               输出每个对象的详细信息
              --help, -h              显示帮助

            退出码：
              0  全部成功
              1  存在失败、冲突、缺失源对象或无法识别的 URL
              2  配置/参数错误
              130 用户中断
            """);
    }
}

public sealed class CommandLineException(string message) : Exception(message);
