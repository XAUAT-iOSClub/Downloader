using System.Collections.Concurrent;
using System.Text;
using System.Threading.Channels;

namespace Downloader.Migrator;

public sealed class MigrationRunner
{
    private static class Statuses
    {
        public const string Copied = "Copied";
        public const string SkippedExisting = "SkippedExisting";
        public const string AlreadyPresent = "AlreadyPresent";
        public const string WouldCopy = "WouldCopy";
        public const string Conflict = "Conflict";
        public const string MissingSource = "MissingSource";
        public const string MissingTarget = "MissingTarget";
        public const string VerifyFailed = "VerifyFailed";
        public const string Failed = "Failed";
        public const string Unresolved = "Unresolved";
        public const string NotSelected = "NotSelected";
        public const string Updated = "Updated";
        public const string Unchanged = "Unchanged";
        public const string Blocked = "Blocked";
        public const string WouldUpdate = "WouldUpdate";
        public const string Disabled = "Disabled";
        public const string NotFound = "NotFound";
        public const string TooLong = "TooLong";
    }

    private readonly MigrationConfig _config;
    private readonly CommandLineOptions _cli;
    private readonly Reporter _reporter;
    private readonly DatabaseAccess _database;
    private readonly StorageKeyMapper _mapper;
    private readonly List<ReferencePlan> _plans = [];
    private readonly ConcurrentDictionary<string, string> _databaseStatuses = new(StringComparer.Ordinal);

    private MigrationStateStore _state = null!;
    private int _processed;
    private int _unresolved;
    private int _dbUpdated;
    private int _dbUnchanged;
    private int _dbBlocked;
    private int _dbFailed;
    private int _dbWouldUpdate;
    private long _bytesCopied;
    private int _sourceDeleted;
    private int _sourceDeleteFailed;

    public MigrationRunner(MigrationConfig config, CommandLineOptions cli, Reporter reporter)
    {
        _config = config;
        _cli = cli;
        _reporter = reporter;
        _database = new DatabaseAccess(config);
        _mapper = new StorageKeyMapper(config.Behavior, config.Source, config.Target);
    }

    public async Task<int> RunAsync(CancellationToken cancellationToken)
    {
        _reporter.Banner("存储迁移：Cloudflare R2 -> 腾讯云 COS");
        _reporter.Info(_config.Describe());
        _reporter.Info(_cli.Apply
            ? "运行模式：APPLY（会复制对象，并按配置更新数据库）"
            : "运行模式：DRY-RUN（只读取和检查，不复制、不写库；确认后加 --apply）");

        if (!_cli.CopyEnabled)
        {
            _reporter.Warn("已启用 --no-copy：不会复制对象，只会在目标对象存在时更新数据库。");
        }

        if (!_config.Behavior.UpdateDatabase)
        {
            _reporter.Warn("已启用 --no-db：只复制对象，不更新数据库。");
        }

        if (_config.Behavior.DeleteSource)
        {
            _reporter.Warn("已启用 --delete-source：每个对象校验通过后会立即删除 R2 源对象。");
        }

        _state = await MigrationStateStore.LoadAsync(_config.Behavior.StateFile, _reporter, cancellationToken);

        try
        {
            return await ExecuteAsync(cancellationToken);
        }
        finally
        {
            await _state.SaveAsync(CancellationToken.None);
        }
    }

    private async Task<int> ExecuteAsync(CancellationToken cancellationToken)
    {
        await _database.TestConnectionAsync(cancellationToken);
        _reporter.Info($"数据库连接正常：{_config.DbProvider}");

        var references = await _database.LoadReferencesAsync(_cli.Scope, _cli.AppId, cancellationToken);
        _reporter.Info(
            $"读取到 {references.Count} 条带存储 URL 的记录：" +
            $"soft {references.Count(x => x.Kind == DbKinds.Soft)}，" +
            $"app-icon {references.Count(x => x.Kind == DbKinds.Icon)}");

        if (references.Count == 0)
        {
            _reporter.Warn("没有需要迁移的数据库记录。");
            return 0;
        }

        _plans.AddRange(references.Select(reference => new ReferencePlan(reference, _mapper.Map(reference.OldUrl))));

        foreach (var plan in _plans.Where(x => !x.Mapping.IsUsable))
        {
            _unresolved++;
            _databaseStatuses[MigrationStateStore.ReferenceKey(plan.Reference.Kind, plan.Reference.Id)] =
                Statuses.Unresolved;
            _reporter.Warn(
                $"无法识别 URL（{plan.Reference.Kind} {plan.Reference.Id}）：{plan.Reference.OldUrl} - {plan.Mapping.Message}");
        }

        var objects = BuildObjectPlans();
        var totalObjects = objects.Count;

        if (_cli.Limit is { } limit && objects.Count > limit)
        {
            objects = objects.Take(limit).ToList();
            _reporter.Info($"--limit {limit}：本次只处理 {objects.Count}/{totalObjects} 个对象，其余记录在报告中标记为 NotSelected。");
        }

        var changedUrls = _plans.Count(x => x.Mapping.IsUsable && x.Mapping.UrlChanged(x.Reference.OldUrl));
        _reporter.Info(
            $"计划处理 {objects.Count} 个存储对象，覆盖 {_plans.Count(x => x.Mapping.IsUsable)} 条数据库记录；" +
            $"其中 {changedUrls} 条 URL 需要改写。");

        if (objects.Count > 0)
        {
            using var source = _cli.CopyEnabled
                ? new S3ObjectStore(_config.Source, _config.Behavior.PartSizeBytes,
                    _config.Behavior.SmallObjectThresholdBytes)
                : null;
            using var target = new S3ObjectStore(_config.Target, _config.Behavior.PartSizeBytes,
                _config.Behavior.SmallObjectThresholdBytes);

            var outcomes = await RunWorkersAsync(objects, source, target, _cli.Apply, cancellationToken);

            if (_cli.Apply && _config.Behavior.UpdateDatabase)
            {
                await UpdateDatabaseAsync(outcomes, apply: true, cancellationToken);
            }
            else if (_config.Behavior.UpdateDatabase)
            {
                await UpdateDatabaseAsync(outcomes, apply: false, cancellationToken);
            }
            else
            {
                await MarkDatabaseDisabledAsync(outcomes, cancellationToken);
            }

            if (_cli.Apply && _config.Behavior.DeleteSource && _cli.CopyEnabled)
            {
                await DeleteSourcesAsync(outcomes, source!, cancellationToken);
            }

            WriteReport(outcomes);
            PrintSummary(outcomes, objects.Count, totalObjects);
            return ComputeExitCode(outcomes);
        }

        WriteReport(new Dictionary<string, ObjectOutcome>(StringComparer.Ordinal));
        PrintSummary(new Dictionary<string, ObjectOutcome>(StringComparer.Ordinal), 0, totalObjects);
        return ComputeExitCode(new Dictionary<string, ObjectOutcome>(StringComparer.Ordinal));
    }

    private List<ObjectPlan> BuildObjectPlans()
    {
        var plans = new List<ObjectPlan>();

        foreach (var group in _plans
                     .Where(x => x.Mapping.IsUsable)
                     .GroupBy(x => x.Mapping.TargetKey, StringComparer.Ordinal))
        {
            var copyReferences = group.Where(x => x.Mapping.NeedsCopy).ToList();
            var needsCopy = copyReferences.Count > 0;

            var sourceKeys = copyReferences
                .Select(x => x.Mapping.SourceKey)
                .Distinct(StringComparer.Ordinal)
                .ToList();

            if (sourceKeys.Count > 1)
            {
                throw new InvalidOperationException(
                    $"目标键 {group.Key} 对应多个源键（{string.Join(", ", sourceKeys)}），拒绝自动合并。");
            }

            plans.Add(new ObjectPlan
            {
                TargetKey = group.Key,
                SourceKey = sourceKeys.FirstOrDefault() ?? group.First().Mapping.SourceKey,
                NeedsCopy = needsCopy,
                CanDeleteSource = group.All(x => x.Mapping.CanDeleteSource)
            });
        }

        return plans.OrderBy(x => x.TargetKey, StringComparer.Ordinal).ToList();
    }

    private async Task<Dictionary<string, ObjectOutcome>> RunWorkersAsync(
        List<ObjectPlan> objects,
        S3ObjectStore? source,
        S3ObjectStore target,
        bool apply,
        CancellationToken cancellationToken)
    {
        var outcomes = new ConcurrentDictionary<string, ObjectOutcome>(StringComparer.Ordinal);
        var channel = Channel.CreateUnbounded<ObjectPlan>(new UnboundedChannelOptions
        {
            SingleReader = false,
            SingleWriter = true
        });

        foreach (var plan in objects)
        {
            channel.Writer.TryWrite(plan);
        }

        channel.Writer.Complete();

        var workerCount = Math.Min(_config.Behavior.Concurrency, Math.Max(1, objects.Count));
        var workers = Enumerable.Range(0, workerCount)
            .Select(_ => Task.Run(async () =>
            {
                await foreach (var plan in channel.Reader.ReadAllAsync(cancellationToken))
                {
                    ObjectOutcome outcome;
                    try
                    {
                        outcome = await ProcessObjectAsync(plan, source, target, apply, cancellationToken);
                    }
                    catch (OperationCanceledException)
                    {
                        throw;
                    }
                    catch (Exception exception)
                    {
                        _reporter.Detail($"对象处理异常 {plan.TargetKey}：{exception}");
                        outcome = ObjectOutcome.Failure(plan, Statuses.Failed, exception.Message);
                    }

                    outcomes[plan.TargetKey] = outcome;
                    var index = Interlocked.Increment(ref _processed);

                    if (outcome.Status == Statuses.Copied)
                    {
                        Interlocked.Add(ref _bytesCopied, outcome.Size);
                    }

                    if (outcome.SourceDeleted)
                    {
                        Interlocked.Increment(ref _sourceDeleted);
                    }

                    LogOutcome(index, objects.Count, outcome);
                    await RecordObjectStateAsync(outcome, cancellationToken);
                }
            }, cancellationToken))
            .ToArray();

        await Task.WhenAll(workers);
        return new Dictionary<string, ObjectOutcome>(outcomes, StringComparer.Ordinal);
    }

    private async Task<ObjectOutcome> ProcessObjectAsync(ObjectPlan plan, S3ObjectStore? source,
        S3ObjectStore target, bool apply, CancellationToken cancellationToken)
    {
        var targetMetadata = await target.HeadAsync(plan.TargetKey, cancellationToken);

        if (!plan.NeedsCopy)
        {
            return targetMetadata == null
                ? ObjectOutcome.Failure(plan, Statuses.MissingTarget,
                    $"目标对象不存在：{plan.TargetKey}")
                : ObjectOutcome.Success(plan, Statuses.AlreadyPresent, targetMetadata.Length,
                    deletePending: apply && _config.Behavior.DeleteSource &&
                                   plan.CanDeleteSource &&
                                   !string.IsNullOrWhiteSpace(plan.SourceKey));
        }

        if (!_cli.CopyEnabled)
        {
            return targetMetadata == null
                ? ObjectOutcome.Failure(plan, Statuses.MissingTarget,
                    $"目标对象不存在，无法只更新数据库：{plan.TargetKey}")
                : ObjectOutcome.Success(plan, Statuses.AlreadyPresent, targetMetadata.Length);
        }

        if (source == null)
        {
            throw new InvalidOperationException("源存储客户端未初始化。");
        }

        var sourceMetadata = await source.HeadAsync(plan.SourceKey, cancellationToken);
        if (sourceMetadata == null)
        {
            return ObjectOutcome.Failure(plan, Statuses.MissingSource,
                $"源对象不存在：{plan.SourceKey}");
        }

        if (targetMetadata != null)
        {
            var canSkip = false;

            if (targetMetadata.Length == sourceMetadata.Length)
            {
                if (apply && _config.Behavior.Verify == VerifyMode.Sha256)
                {
                    var compared = await CompareSha256Async(plan, source, target, cancellationToken);
                    if (!compared.Success)
                    {
                        if (!_cli.Force)
                        {
                            return ObjectOutcome.Failure(plan, Statuses.VerifyFailed, compared.Error!);
                        }

                        _reporter.Warn(
                            $"目标对象大小一致但 SHA256 不一致，--force 将覆盖：{plan.TargetKey}");
                    }
                    else
                    {
                        canSkip = true;
                    }
                }
                else
                {
                    canSkip = true;
                }
            }
            else if (!_cli.Force)
            {
                return ObjectOutcome.Failure(plan, Statuses.Conflict,
                    $"目标已存在且大小不同（源 {sourceMetadata.Length}，目标 {targetMetadata.Length}）；" +
                    "确认后使用 --force 覆盖。");
            }

            if (canSkip)
            {
                return ObjectOutcome.Success(plan, Statuses.SkippedExisting, sourceMetadata.Length,
                    deletePending: apply && _config.Behavior.DeleteSource && plan.CanDeleteSource);
            }
        }

        if (!apply)
        {
            return ObjectOutcome.Success(plan, Statuses.WouldCopy, sourceMetadata.Length);
        }

        await using var sourceObject = await source.OpenReadAsync(plan.SourceKey, cancellationToken);
        if (sourceObject == null)
        {
            return ObjectOutcome.Failure(plan, Statuses.MissingSource,
                $"源对象在读取时消失：{plan.SourceKey}");
        }

        var length = sourceObject.Metadata.Length;
        if (length < 0 || (length == 0 && sourceMetadata.Length > 0))
        {
            length = sourceMetadata.Length;
        }
        if (length < 0)
        {
            return ObjectOutcome.Failure(plan, Statuses.Failed, "无法读取源对象大小。");
        }

        var contentType = sourceObject.Metadata.ContentType;
        if (string.IsNullOrWhiteSpace(contentType) ||
            string.Equals(contentType, "application/octet-stream", StringComparison.OrdinalIgnoreCase))
        {
            contentType = ContentTypeHelper.Get(plan.TargetKey);
        }

        await target.UploadAsync(plan.TargetKey, sourceObject.Stream, length, contentType,
            sourceObject.Metadata.CacheControl, cancellationToken);

        var verification = await VerifyAfterCopyAsync(plan, length, source, target, cancellationToken);
        if (!verification.Success)
        {
            return ObjectOutcome.Failure(plan, Statuses.VerifyFailed, verification.Error!);
        }

        return ObjectOutcome.Success(plan, Statuses.Copied, length,
            deletePending: _config.Behavior.DeleteSource && plan.CanDeleteSource);
    }

    private async Task<(bool Success, string? Error)> VerifyAfterCopyAsync(ObjectPlan plan, long expectedLength,
        S3ObjectStore source, S3ObjectStore target, CancellationToken cancellationToken)
    {
        var targetMetadata = await target.HeadAsync(plan.TargetKey, cancellationToken);
        if (targetMetadata == null)
        {
            return (false, "复制完成后目标对象不存在。");
        }

        if (targetMetadata.Length != expectedLength)
        {
            return (false, $"复制后大小不一致：期望 {expectedLength}，实际 {targetMetadata.Length}。");
        }

        if (_config.Behavior.Verify != VerifyMode.Sha256)
        {
            return (true, null);
        }

        return await CompareSha256Async(plan, source, target, cancellationToken);
    }

    private async Task<(bool Success, string? Error)> CompareSha256Async(ObjectPlan plan, S3ObjectStore source,
        S3ObjectStore target, CancellationToken cancellationToken)
    {
        await using var sourceObject = await source.OpenReadAsync(plan.SourceKey, cancellationToken);
        if (sourceObject == null)
        {
            return (false, $"计算 SHA256 时源对象不存在：{plan.SourceKey}");
        }

        await using var targetObject = await target.OpenReadAsync(plan.TargetKey, cancellationToken);
        if (targetObject == null)
        {
            return (false, $"计算 SHA256 时目标对象不存在：{plan.TargetKey}");
        }

        var sourceHash = await S3ObjectStore.ComputeSha256Async(sourceObject.Stream, cancellationToken);
        var targetHash = await S3ObjectStore.ComputeSha256Async(targetObject.Stream, cancellationToken);
        _reporter.Detail($"SHA256 {plan.TargetKey}: 源 {sourceHash} / 目标 {targetHash}");

        return string.Equals(sourceHash, targetHash, StringComparison.Ordinal)
            ? (true, null)
            : (false, $"SHA256 不一致：源 {sourceHash}，目标 {targetHash}。");
    }

    private async Task UpdateDatabaseAsync(IReadOnlyDictionary<string, ObjectOutcome> outcomes, bool apply,
        CancellationToken cancellationToken)
    {
        foreach (var plan in _plans)
        {
            if (!plan.Mapping.IsUsable)
            {
                await SetDatabaseStatusAsync(plan, Statuses.Unresolved, cancellationToken);
                continue;
            }

            if (!outcomes.TryGetValue(plan.Mapping.TargetKey, out var outcome))
            {
                await SetDatabaseStatusAsync(plan, Statuses.NotSelected, cancellationToken);
                continue;
            }

            if (!outcome.IsSuccess)
            {
                Interlocked.Increment(ref _dbBlocked);
                await SetDatabaseStatusAsync(plan, Statuses.Blocked, cancellationToken);
                continue;
            }

            if (!plan.Mapping.UrlChanged(plan.Reference.OldUrl))
            {
                Interlocked.Increment(ref _dbUnchanged);
                await SetDatabaseStatusAsync(plan, Statuses.Unchanged, cancellationToken);
                continue;
            }

            // SoftUrl/IconUrl 在模型和数据库里都是 512 字符上限，提前给出清晰错误。
            if (plan.Mapping.NewUrl.Length > 512)
            {
                Interlocked.Increment(ref _dbFailed);
                _reporter.Error(
                    $"新 URL 超过数据库字段上限（512）：{plan.Reference.Kind} {plan.Reference.Id}，" +
                    $"长度 {plan.Mapping.NewUrl.Length}");
                await SetDatabaseStatusAsync(plan, Statuses.TooLong, cancellationToken);
                continue;
            }

            if (!apply)
            {
                Interlocked.Increment(ref _dbWouldUpdate);
                _reporter.Detail($"[dry-run] 将更新 {plan.Reference.Kind} {plan.Reference.Id}：{plan.Mapping.NewUrl}");
                await SetDatabaseStatusAsync(plan, Statuses.WouldUpdate, cancellationToken);
                continue;
            }

            try
            {
                var updated = await _database.UpdateUrlAsync(plan.Reference, plan.Mapping.NewUrl, cancellationToken);
                if (updated)
                {
                    Interlocked.Increment(ref _dbUpdated);
                    _reporter.Success(
                        $"数据库已更新 {plan.Reference.Kind} {plan.Reference.Id} -> {plan.Mapping.NewUrl}");
                    await SetDatabaseStatusAsync(plan, Statuses.Updated, cancellationToken);
                }
                else
                {
                    Interlocked.Increment(ref _dbFailed);
                    _reporter.Error($"数据库记录不存在，无法更新：{plan.Reference.Kind} {plan.Reference.Id}");
                    await SetDatabaseStatusAsync(plan, Statuses.NotFound, cancellationToken);
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception exception)
            {
                Interlocked.Increment(ref _dbFailed);
                _reporter.Error(
                    $"数据库更新失败 {plan.Reference.Kind} {plan.Reference.Id}：{exception.Message}");
                await SetDatabaseStatusAsync(plan, Statuses.Failed, cancellationToken);
            }
        }
    }

    private async Task MarkDatabaseDisabledAsync(IReadOnlyDictionary<string, ObjectOutcome> outcomes,
        CancellationToken cancellationToken)
    {
        foreach (var plan in _plans)
        {
            var status = plan.Mapping.IsUsable &&
                         outcomes.TryGetValue(plan.Mapping.TargetKey, out var outcome) &&
                         outcome.IsSuccess
                ? Statuses.Disabled
                : Statuses.NotSelected;
            await SetDatabaseStatusAsync(plan, status, cancellationToken);
        }
    }

    private async Task SetDatabaseStatusAsync(ReferencePlan plan, string status,
        CancellationToken cancellationToken)
    {
        _databaseStatuses[MigrationStateStore.ReferenceKey(plan.Reference.Kind, plan.Reference.Id)] = status;
        await RecordDatabaseStateAsync(plan, status, cancellationToken);
    }

    private async Task RecordObjectStateAsync(ObjectOutcome outcome, CancellationToken cancellationToken)
    {
        var key = MigrationStateStore.ObjectKey(outcome.Plan.TargetKey);
        var previous = _state.Get(key);

        await _state.RecordAsync(key, new MigrationStateEntry
        {
            Kind = "object",
            Id = outcome.Plan.TargetKey,
            SourceKey = outcome.Plan.SourceKey,
            TargetKey = outcome.Plan.TargetKey,
            Status = outcome.Status,
            Error = outcome.Error ?? "",
            Size = outcome.Size,
            Attempts = (previous?.Attempts ?? 0) + 1,
            UpdatedAt = DateTimeOffset.Now
        }, cancellationToken);
    }

    private async Task RecordDatabaseStateAsync(ReferencePlan plan, string status,
        CancellationToken cancellationToken)
    {
        var key = MigrationStateStore.ReferenceKey(plan.Reference.Kind, plan.Reference.Id);
        var previous = _state.Get(key);

        await _state.RecordAsync(key, new MigrationStateEntry
        {
            Kind = plan.Reference.Kind,
            Id = plan.Reference.Id,
            SourceKey = plan.Mapping.SourceKey,
            TargetKey = plan.Mapping.TargetKey,
            OldUrl = plan.Reference.OldUrl,
            NewUrl = plan.Mapping.NewUrl,
            Status = status,
            Size = 0,
            Attempts = (previous?.Attempts ?? 0) + 1,
            UpdatedAt = DateTimeOffset.Now
        }, cancellationToken);
    }

    private void LogOutcome(int index, int total, ObjectOutcome outcome)
    {
        var prefix = $"[{index}/{total}]";
        var size = FormatBytes(outcome.Size);

        switch (outcome.Status)
        {
            case Statuses.Copied:
                _reporter.Success($"{prefix} 已复制 {size} -> {outcome.Plan.TargetKey}");
                break;
            case Statuses.WouldCopy:
                _reporter.Info($"{prefix} 将复制 {size} -> {outcome.Plan.TargetKey}");
                break;
            case Statuses.SkippedExisting:
                _reporter.Detail($"{prefix} 目标已存在且大小一致，跳过 {outcome.Plan.TargetKey}");
                break;
            case Statuses.AlreadyPresent:
                _reporter.Detail($"{prefix} 已指向目标存储，跳过 {outcome.Plan.TargetKey}");
                break;
            default:
                _reporter.Error($"{prefix} {outcome.Status} {outcome.Plan.TargetKey}：{outcome.Error}");
                break;
        }
    }

    private async Task DeleteSourcesAsync(IReadOnlyDictionary<string, ObjectOutcome> outcomes,
        S3ObjectStore source, CancellationToken cancellationToken)
    {
        foreach (var outcome in outcomes.Values.Where(x => x.DeletePending))
        {
            if (_config.Behavior.UpdateDatabase)
            {
                var relatedReferences = _plans.Where(x =>
                    x.Mapping.IsUsable &&
                    string.Equals(x.Mapping.TargetKey, outcome.Plan.TargetKey, StringComparison.Ordinal));

                var blocked = relatedReferences.Any(x =>
                {
                    var status = _databaseStatuses.GetValueOrDefault(
                        MigrationStateStore.ReferenceKey(x.Reference.Kind, x.Reference.Id));
                    return status is not (Statuses.Updated or Statuses.Unchanged);
                });

                if (blocked)
                {
                    _reporter.Warn(
                        $"数据库状态未确认，暂不删除源对象：{outcome.Plan.SourceKey}");
                    continue;
                }
            }

            try
            {
                var sourceMetadata = await source.HeadAsync(outcome.Plan.SourceKey, cancellationToken);
                if (sourceMetadata == null)
                {
                    _reporter.Detail($"源对象已不存在，无需删除：{outcome.Plan.SourceKey}");
                    continue;
                }

                if (outcome.Size > 0 && sourceMetadata.Length != outcome.Size)
                {
                    Interlocked.Increment(ref _sourceDeleteFailed);
                    _reporter.Error(
                        $"源对象大小与目标不一致，未删除：{outcome.Plan.SourceKey} " +
                        $"（源 {sourceMetadata.Length}，目标 {outcome.Size}）");
                    continue;
                }

                await source.DeleteAsync(outcome.Plan.SourceKey, cancellationToken);
                outcome.SourceDeleted = true;
                Interlocked.Increment(ref _sourceDeleted);
                _reporter.Detail($"已删除源对象：{outcome.Plan.SourceKey}");
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception exception)
            {
                Interlocked.Increment(ref _sourceDeleteFailed);
                _reporter.Error(
                    $"删除源对象失败 {outcome.Plan.SourceKey}：{exception.Message}");
            }
        }
    }

    private void WriteReport(IReadOnlyDictionary<string, ObjectOutcome> outcomes)
    {
        try
        {
            var directory = Path.GetDirectoryName(_config.Behavior.ReportFile);
            if (!string.IsNullOrWhiteSpace(directory))
            {
                Directory.CreateDirectory(directory);
            }

            using var writer = new StreamWriter(_config.Behavior.ReportFile, append: false,
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
            writer.WriteLine("kind,id,name,appId,oldUrl,newUrl,sourceKey,targetKey,size,objectStatus,dbStatus,sourceDeleted,error");

            foreach (var plan in _plans)
            {
                var referenceKey = MigrationStateStore.ReferenceKey(plan.Reference.Kind, plan.Reference.Id);
                var outcome = outcomes.GetValueOrDefault(plan.Mapping.TargetKey);
                var dbStatus = _databaseStatuses.GetValueOrDefault(referenceKey) ?? Statuses.NotSelected;

                writer.WriteLine(string.Join(',',
                    Csv(plan.Reference.Kind),
                    Csv(plan.Reference.Id),
                    Csv(plan.Reference.Name),
                    Csv(plan.Reference.AppId ?? ""),
                    Csv(plan.Reference.OldUrl),
                    Csv(plan.Mapping.NewUrl),
                    Csv(plan.Mapping.SourceKey),
                    Csv(plan.Mapping.TargetKey),
                    outcome?.Size.ToString() ?? "",
                    Csv(outcome?.Status ?? Statuses.NotSelected),
                    Csv(dbStatus),
                    Csv(outcome?.SourceDeleted == true ? "true" : "false"),
                    Csv(outcome?.Error ?? plan.Mapping.Message)));
            }

            _reporter.Info($"CSV 报告：{_config.Behavior.ReportFile}");
        }
        catch (Exception exception)
        {
            _reporter.Warn($"写入 CSV 报告失败：{exception.Message}");
        }
    }

    private void PrintSummary(IReadOnlyDictionary<string, ObjectOutcome> outcomes, int selectedObjects,
        int totalObjects)
    {
        var copied = Count(outcomes, Statuses.Copied);
        var skipped = Count(outcomes, Statuses.SkippedExisting);
        var present = Count(outcomes, Statuses.AlreadyPresent);
        var wouldCopy = Count(outcomes, Statuses.WouldCopy);
        var conflicts = Count(outcomes, Statuses.Conflict);
        var missingSource = Count(outcomes, Statuses.MissingSource);
        var missingTarget = Count(outcomes, Statuses.MissingTarget);
        var verifyFailed = Count(outcomes, Statuses.VerifyFailed);
        var failed = Count(outcomes, Statuses.Failed);

        _reporter.Banner("迁移结果");
        _reporter.Info(
            $"对象：计划 {selectedObjects}/{totalObjects}，已复制 {copied}，已存在跳过 {skipped + present}，" +
            $"待复制（dry-run）{wouldCopy}");
        _reporter.Info(
            $"异常：冲突 {conflicts}，源缺失 {missingSource}，目标缺失 {missingTarget}，" +
            $"校验失败 {verifyFailed}，失败 {failed}");
        _reporter.Info(
            $"复制数据量：{FormatBytes(_bytesCopied)}，删除源对象：{_sourceDeleted}，删除失败：{_sourceDeleteFailed}");
        _reporter.Info(
            $"数据库：已更新 {_dbUpdated}，无需改动 {_dbUnchanged}，因对象失败阻塞 {_dbBlocked}，" +
            $"失败 {_dbFailed}，待更新（dry-run）{_dbWouldUpdate}");
        _reporter.Info($"无法识别的 URL：{_unresolved}");
        _reporter.Info($"状态文件：{_config.Behavior.StateFile}");
    }

    private int ComputeExitCode(IReadOnlyDictionary<string, ObjectOutcome> outcomes)
    {
        var blocking = Count(outcomes, Statuses.Conflict) +
                       Count(outcomes, Statuses.MissingSource) +
                       Count(outcomes, Statuses.MissingTarget) +
                       Count(outcomes, Statuses.VerifyFailed) +
                       Count(outcomes, Statuses.Failed) +
                       _dbFailed +
                       _sourceDeleteFailed;

        if (!_cli.IgnoreUnresolved)
        {
            blocking += _unresolved;
        }

        return blocking == 0 ? 0 : 1;
    }

    private static int Count(IReadOnlyDictionary<string, ObjectOutcome> outcomes, string status)
    {
        return outcomes.Values.Count(x => x.Status == status);
    }

    private static string Csv(string value)
    {
        return $"\"{value.Replace("\"", "\"\"")}\"";
    }

    private static string FormatBytes(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        double value = bytes;
        var unit = 0;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }

        return $"{value:0.##} {units[unit]}";
    }

    private sealed record ReferencePlan(DbReference Reference, KeyMapping Mapping);

    private sealed class ObjectPlan
    {
        public required string TargetKey { get; init; }
        public required string SourceKey { get; init; }
        public required bool NeedsCopy { get; init; }
        public required bool CanDeleteSource { get; init; }
    }

    private sealed class ObjectOutcome
    {
        public required ObjectPlan Plan { get; init; }
        public required string Status { get; init; }
        public long Size { get; init; }
        public string? Error { get; init; }
        public bool DeletePending { get; init; }
        public bool SourceDeleted { get; set; }

        public bool IsSuccess => Status is Statuses.Copied or Statuses.SkippedExisting or
            Statuses.AlreadyPresent or Statuses.WouldCopy;

        public static ObjectOutcome Success(ObjectPlan plan, string status, long size,
            bool deletePending = false)
        {
            return new ObjectOutcome
            {
                Plan = plan,
                Status = status,
                Size = size,
                DeletePending = deletePending
            };
        }

        public static ObjectOutcome Failure(ObjectPlan plan, string status, string error)
        {
            return new ObjectOutcome
            {
                Plan = plan,
                Status = status,
                Error = error
            };
        }
    }
}
