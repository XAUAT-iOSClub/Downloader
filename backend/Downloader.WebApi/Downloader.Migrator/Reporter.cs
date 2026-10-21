using System.Text;

namespace Downloader.Migrator;

/// <summary>
/// 线程安全的控制台/文件日志。不会输出任何密钥。
/// </summary>
public sealed class Reporter : IDisposable
{
    private const string Reset = "\u001b[0m";
    private const string Gray = "\u001b[90m";
    private const string Green = "\u001b[32m";
    private const string Yellow = "\u001b[33m";
    private const string Red = "\u001b[31m";
    private const string Cyan = "\u001b[36m";

    private readonly object _gate = new();
    private readonly StreamWriter? _fileWriter;
    private readonly bool _useColors;

    public Reporter(string? logFile, bool verbose)
    {
        Verbose = verbose;
        _useColors = !Console.IsOutputRedirected &&
                     string.IsNullOrEmpty(Environment.GetEnvironmentVariable("NO_COLOR"));

        if (!string.IsNullOrWhiteSpace(logFile))
        {
            try
            {
                var directory = Path.GetDirectoryName(logFile);
                if (!string.IsNullOrWhiteSpace(directory))
                {
                    Directory.CreateDirectory(directory);
                }

                _fileWriter = new StreamWriter(logFile, append: true, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false))
                {
                    AutoFlush = true
                };
            }
            catch (Exception exception)
            {
                Console.Error.WriteLine($"无法打开日志文件 {logFile}，将只输出到控制台：{exception.Message}");
            }
        }
    }

    public bool Verbose { get; }

    public void Info(string message) => Write("INFO", message, null);

    public void Success(string message) => Write(" OK ", message, Green);

    public void Warn(string message) => Write("WARN", message, Yellow);

    public void Error(string message) => Write("FAIL", message, Red);

    public void Detail(string message)
    {
        if (Verbose)
        {
            Write("DBG ", message, Gray);
        }
    }

    public void Banner(string message) => Write("====", message, Cyan);

    private void Write(string level, string message, string? color)
    {
        var line = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] [{level}] {message}";

        lock (_gate)
        {
            if (_useColors && color != null)
            {
                Console.Out.Write(color);
                Console.Out.WriteLine(line);
                Console.Out.Write(Reset);
            }
            else
            {
                Console.Out.WriteLine(line);
            }

            _fileWriter?.WriteLine(line);
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _fileWriter?.Dispose();
        }
    }
}
