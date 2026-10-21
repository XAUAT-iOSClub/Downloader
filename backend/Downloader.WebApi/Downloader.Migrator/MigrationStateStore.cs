using System.Text.Encodings.Web;
using System.Text.Json;

namespace Downloader.Migrator;

public sealed class MigrationState
{
    public int Version { get; set; } = 1;
    public DateTimeOffset StartedAt { get; set; } = DateTimeOffset.Now;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.Now;
    public Dictionary<string, MigrationStateEntry> Entries { get; set; } = new(StringComparer.Ordinal);
}

public sealed class MigrationStateEntry
{
    public string Kind { get; set; } = "";
    public string Id { get; set; } = "";
    public string SourceKey { get; set; } = "";
    public string TargetKey { get; set; } = "";
    public string OldUrl { get; set; } = "";
    public string NewUrl { get; set; } = "";
    public string Status { get; set; } = "";
    public string Error { get; set; } = "";
    public long Size { get; set; }
    public int Attempts { get; set; }
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.Now;
}

/// <summary>
/// 断点/审计状态。真正的幂等判断仍然以目标对象的 HEAD 结果为准，
/// 这个文件用于记录每次尝试，方便排查和中断后继续。
/// </summary>
public sealed class MigrationStateStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    private readonly object _gate = new();
    private readonly SemaphoreSlim _saveGate = new(1, 1);
    private readonly string _path;
    private readonly Reporter _reporter;
    private MigrationState _state = new();
    private DateTimeOffset _lastSave = DateTimeOffset.MinValue;
    private int _pendingWrites;

    private MigrationStateStore(string path, Reporter reporter)
    {
        _path = path;
        _reporter = reporter;
    }

    public string Path => _path;
    public MigrationState State => _state;

    public static async Task<MigrationStateStore> LoadAsync(string path, Reporter reporter,
        CancellationToken cancellationToken)
    {
        var store = new MigrationStateStore(path, reporter);

        if (!File.Exists(path))
        {
            store._state = new MigrationState();
            return store;
        }

        try
        {
            await using var stream = File.OpenRead(path);
            var state = await JsonSerializer.DeserializeAsync<MigrationState>(stream, JsonOptions, cancellationToken);
            if (state == null)
            {
                throw new JsonException("状态文件内容为空。");
            }

            state.Entries ??= new Dictionary<string, MigrationStateEntry>(StringComparer.Ordinal);
            store._state = state;
            reporter.Info($"已加载状态文件：{path}（{state.Entries.Count} 条历史记录）");
        }
        catch (Exception exception) when (exception is JsonException or IOException or NotSupportedException)
        {
            var backup = $"{path}.corrupt-{DateTime.Now:yyyyMMddHHmmss}";
            try
            {
                File.Move(path, backup, overwrite: true);
            }
            catch
            {
                // 备份失败不影响新状态继续写。
            }

            reporter.Warn($"状态文件无法解析，已备份为 {backup}，本次重新开始：{exception.Message}");
            store._state = new MigrationState();
        }

        return store;
    }

    public MigrationStateEntry? Get(string key)
    {
        lock (_gate)
        {
            return _state.Entries.TryGetValue(key, out var entry) ? entry : null;
        }
    }

    public async Task RecordAsync(string key, MigrationStateEntry entry, CancellationToken cancellationToken,
        bool forceSave = false)
    {
        int pending;
        lock (_gate)
        {
            _state.Entries[key] = entry;
            _state.UpdatedAt = DateTimeOffset.Now;
            _pendingWrites++;
            pending = _pendingWrites;
        }

        var shouldSave = forceSave ||
                         pending >= 20 ||
                         DateTimeOffset.Now - _lastSave > TimeSpan.FromSeconds(2);

        if (shouldSave)
        {
            await SaveAsync(cancellationToken);
        }
    }

    public async Task SaveAsync(CancellationToken cancellationToken)
    {
        await _saveGate.WaitAsync(cancellationToken);
        try
        {
            MigrationState snapshot;
            lock (_gate)
            {
                _state.UpdatedAt = DateTimeOffset.Now;
                snapshot = new MigrationState
                {
                    Version = _state.Version,
                    StartedAt = _state.StartedAt,
                    UpdatedAt = _state.UpdatedAt,
                    Entries = new Dictionary<string, MigrationStateEntry>(_state.Entries, StringComparer.Ordinal)
                };
                _pendingWrites = 0;
                _lastSave = DateTimeOffset.Now;
            }

            var directory = System.IO.Path.GetDirectoryName(_path);
            if (!string.IsNullOrWhiteSpace(directory))
            {
                Directory.CreateDirectory(directory);
            }

            var temporaryPath = $"{_path}.tmp";
            await using (var stream = File.Create(temporaryPath))
            {
                await JsonSerializer.SerializeAsync(stream, snapshot, JsonOptions, cancellationToken);
            }

            File.Move(temporaryPath, _path, overwrite: true);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            _reporter.Warn($"写入状态文件失败：{exception.Message}");
        }
        finally
        {
            _saveGate.Release();
        }
    }

    public static string ObjectKey(string targetKey) => $"object:{targetKey}";
    public static string ReferenceKey(string kind, string id) => $"db:{kind}:{id}";
}
