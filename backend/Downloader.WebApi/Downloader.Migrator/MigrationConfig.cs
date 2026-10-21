using System.Globalization;
using System.Text.Json.Serialization;
using DotNetEnv;

namespace Downloader.Migrator;

public sealed class MigrationConfig
{
    public required string ConnectionString { get; init; }
    public required string DbProvider { get; init; }
    public required StorageEndpointConfig Source { get; init; }
    public required StorageEndpointConfig Target { get; init; }
    public required MigrationBehavior Behavior { get; init; }
    public string? ConfigFilePath { get; init; }

    public string Describe()
    {
        return $"""
            配置：{ConfigFilePath ?? "(仅环境变量)"}
              数据库：{DbProvider}
              源存储：{Source.Describe()}
              目标存储：{Target.Describe()}
              旧 URL 前缀：{string.Join(" | ", Behavior.OldPublicBases)}
              新 URL 前缀：{Behavior.NewPublicBaseUrl}
              KeyPrefix：{DisplayPrefix(Source.KeyPrefix)} -> {DisplayPrefix(Target.KeyPrefix)}
              并发：{Behavior.Concurrency}，分片：{Behavior.PartSizeBytes / 1024 / 1024} MB，校验：{Behavior.Verify}
              更新数据库：{Behavior.UpdateDatabase}，删除源对象：{Behavior.DeleteSource}
            """;
    }

    private static string DisplayPrefix(string prefix) => string.IsNullOrWhiteSpace(prefix) ? "(无)" : prefix;
}

public sealed class StorageEndpointConfig
{
    public required string Name { get; init; }
    public required string Endpoint { get; init; }
    public required string Bucket { get; init; }
    public required string Region { get; init; }
    public required string AccessKey { get; init; }
    public required string SecretKey { get; init; }
    public string KeyPrefix { get; init; } = "";
    public bool ForcePathStyle { get; init; }
    public bool DisableChecksum { get; init; }
    public bool DisablePayloadSigning { get; init; }
    public int TimeoutMinutes { get; init; } = 30;
    public int MaxRetries { get; init; } = 5;

    [JsonIgnore]
    public string EndpointHost => Uri.TryCreate(Endpoint, UriKind.Absolute, out var uri) ? uri.Host : Endpoint;

    public string Describe()
    {
        var prefix = string.IsNullOrWhiteSpace(KeyPrefix) ? "(无)" : KeyPrefix;
        return $"{Name} endpoint={Endpoint} bucket={Bucket} region={Region} keyPrefix={prefix} pathStyle={ForcePathStyle}";
    }
}

public sealed class MigrationBehavior
{
    public required IReadOnlyList<string> OldPublicBases { get; init; }
    public required IReadOnlyList<string> TargetPublicBases { get; init; }
    public required string NewPublicBaseUrl { get; init; }
    public int Concurrency { get; init; } = 4;
    public long PartSizeBytes { get; init; } = 16L * 1024 * 1024;
    public long SmallObjectThresholdBytes { get; init; } = 8L * 1024 * 1024;
    public VerifyMode Verify { get; init; } = VerifyMode.Size;
    public bool UpdateDatabase { get; init; } = true;
    public bool DeleteSource { get; init; }
    public required string StateFile { get; init; }
    public required string ReportFile { get; init; }
    public required string LogFile { get; init; }
}

public static class MigrationConfigLoader
{
    private static readonly string[] DefaultConfigNames = [".env.migrate"];

    public static MigrationConfig Load(CommandLineOptions cli)
    {
        var configPath = ResolveConfigPath(cli.ConfigPath);
        if (configPath != null)
        {
            try
            {
                Env.Load(configPath, new LoadOptions(setEnvVars: true, clobberExistingVars: false));
            }
            catch (Exception exception)
            {
                throw new ConfigurationException($"读取配置文件 {configPath} 失败：{exception.Message}");
            }
        }

        var workingDirectory = configPath != null
            ? Path.GetDirectoryName(configPath)!
            : Directory.GetCurrentDirectory();

        var errors = new List<string>();

        var connectionString = Read("SQL", "ConnectionStrings__DefaultConnection");
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            errors.Add("缺少 SQL 或 ConnectionStrings__DefaultConnection。");
        }

        var dbProvider = (Read("MIGRATION__DB_PROVIDER") ?? "auto").Trim().ToLowerInvariant();
        if (dbProvider is not ("auto" or "postgres" or "postgresql" or "npgsql" or "sqlite"))
        {
            errors.Add($"MIGRATION__DB_PROVIDER 不支持 {dbProvider}，可选 auto/postgres/sqlite。");
        }

        var source = ReadEndpoint(
            "源存储",
            new EndpointKeys("SOURCE", "Storage__S3"),
            errors,
            defaultRegion: "auto",
            defaultForcePathStyle: true,
            defaultDisableChecksum: true);

        var target = ReadEndpoint(
            "目标存储",
            new EndpointKeys("TARGET", null),
            errors,
            defaultRegion: "",
            defaultForcePathStyle: false,
            defaultDisableChecksum: true);

        if (target != null && string.IsNullOrWhiteSpace(target.Endpoint) && !string.IsNullOrWhiteSpace(target.Region))
        {
            target = CopyEndpoint(target, endpoint: $"https://cos.{target.Region}.myqcloud.com");
        }

        if (source != null && target != null &&
            string.Equals(source.EndpointHost, target.EndpointHost, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(source.Bucket, target.Bucket, StringComparison.Ordinal))
        {
            errors.Add("源和目标是同一个 endpoint + bucket，继续执行会覆盖源数据，已终止。");
        }

        var oldPublicBases = ReadPublicBases("MIGRATION__OLD_PUBLIC_BASE");
        if (oldPublicBases.Count == 0)
        {
            var storagePublicBase = Read("Storage__PublicBaseUrl");
            var provider = Read("Storage__Provider") ?? "";
            if (!string.IsNullOrWhiteSpace(storagePublicBase))
            {
                var trimmed = storagePublicBase.Trim().TrimEnd('/');
                if (provider.Equals("S3", StringComparison.OrdinalIgnoreCase) ||
                    provider.Equals("R2", StringComparison.OrdinalIgnoreCase) ||
                    storagePublicBase.Contains("/files", StringComparison.OrdinalIgnoreCase))
                {
                    oldPublicBases.Add($"{trimmed}/files");
                }
                else
                {
                    oldPublicBases.Add(trimmed);
                }
            }
        }

        if (oldPublicBases.Count == 0)
        {
            errors.Add("缺少 MIGRATION__OLD_PUBLIC_BASE，或可从中推导的 Storage__PublicBaseUrl。");
        }

        foreach (var oldBase in oldPublicBases)
        {
            if (!IsHttpUrl(oldBase))
            {
                errors.Add($"MIGRATION__OLD_PUBLIC_BASE 不是合法的 HTTP(S) URL：{oldBase}");
            }
        }

        var newPublicBase = Read("MIGRATION__NEW_PUBLIC_BASE");
        if (string.IsNullOrWhiteSpace(newPublicBase))
        {
            newPublicBase = oldPublicBases.FirstOrDefault();
        }

        if (string.IsNullOrWhiteSpace(newPublicBase))
        {
            errors.Add("缺少 MIGRATION__NEW_PUBLIC_BASE。");
        }
        else if (!Uri.TryCreate(newPublicBase, UriKind.Absolute, out var newBaseUri) ||
                 (newBaseUri.Scheme != Uri.UriSchemeHttp && newBaseUri.Scheme != Uri.UriSchemeHttps))
        {
            errors.Add($"MIGRATION__NEW_PUBLIC_BASE 不是合法的 HTTP(S) URL：{newPublicBase}");
        }

        var targetPublicBases = ReadPublicBases("MIGRATION__TARGET_PUBLIC_BASE");
        if (!string.IsNullOrWhiteSpace(newPublicBase))
        {
            targetPublicBases.Insert(0, newPublicBase.Trim().TrimEnd('/'));
        }

        foreach (var targetBase in targetPublicBases)
        {
            if (!IsHttpUrl(targetBase))
            {
                errors.Add($"MIGRATION__TARGET_PUBLIC_BASE 不是合法的 HTTP(S) URL：{targetBase}");
            }
        }

        var partSizeMb = ReadInt("MIGRATION__PART_SIZE_MB", 16, errors);
        if (partSizeMb is < 5 or > 512)
        {
            errors.Add("MIGRATION__PART_SIZE_MB 必须在 5 到 512 之间。");
            partSizeMb = 16;
        }

        var smallObjectMb = ReadInt("MIGRATION__SMALL_OBJECT_MB", 8, errors);
        if (smallObjectMb is < 1 or > 512)
        {
            errors.Add("MIGRATION__SMALL_OBJECT_MB 必须在 1 到 512 之间。");
            smallObjectMb = 8;
        }

        var concurrency = cli.Concurrency ?? ReadInt("MIGRATION__CONCURRENCY", 4, errors);
        if (concurrency is < 1 or > 32)
        {
            errors.Add("MIGRATION__CONCURRENCY 必须在 1 到 32 之间。");
            concurrency = 4;
        }

        var verify = cli.Verify ?? ReadVerify("MIGRATION__VERIFY", errors);
        var updateDatabase = cli.UpdateDatabase && ReadBool("MIGRATION__UPDATE_DATABASE", true);
        var deleteSource = cli.DeleteSource || ReadBool("MIGRATION__DELETE_SOURCE", false);

        var stateFile = ResolvePath(
            cli.StateFile ?? Read("MIGRATION__STATE_FILE") ?? "migration-state.json",
            workingDirectory);
        var reportFile = ResolvePath(
            cli.ReportFile ?? Read("MIGRATION__REPORT_FILE") ??
            $"migration-report-{DateTime.Now:yyyyMMdd-HHmmss}.csv",
            workingDirectory);
        var logFile = ResolvePath(
            cli.LogFile ?? Read("MIGRATION__LOG_FILE") ?? "migration.log",
            workingDirectory);

        if (errors.Count > 0)
        {
            throw new ConfigurationException(string.Join(Environment.NewLine, errors));
        }

        return new MigrationConfig
        {
            ConnectionString = connectionString!,
            DbProvider = NormalizeDbProvider(dbProvider, connectionString!),
            Source = source!,
            Target = target!,
            ConfigFilePath = configPath,
            Behavior = new MigrationBehavior
            {
                OldPublicBases = oldPublicBases
                    .Distinct(StringComparer.Ordinal)
                    .OrderByDescending(x => x.Length)
                    .ToArray(),
                TargetPublicBases = targetPublicBases
                    .Distinct(StringComparer.Ordinal)
                    .OrderByDescending(x => x.Length)
                    .ToArray(),
                NewPublicBaseUrl = newPublicBase!.Trim().TrimEnd('/'),
                Concurrency = concurrency,
                PartSizeBytes = partSizeMb * 1024L * 1024L,
                SmallObjectThresholdBytes = smallObjectMb * 1024L * 1024L,
                Verify = verify,
                UpdateDatabase = updateDatabase,
                DeleteSource = deleteSource,
                StateFile = stateFile,
                ReportFile = reportFile,
                LogFile = logFile
            }
        };
    }

    private static StorageEndpointConfig? ReadEndpoint(
        string label,
        EndpointKeys keys,
        List<string> errors,
        string defaultRegion,
        bool defaultForcePathStyle,
        bool defaultDisableChecksum)
    {
        var endpoint = Read($"{keys.Primary}__ENDPOINT", $"{keys.Primary}__SERVICE_URL", keys.LegacyServiceUrl);
        var bucket = Read($"{keys.Primary}__BUCKET", keys.LegacyBucket);
        var region = Read($"{keys.Primary}__REGION", keys.LegacyRegion);
        var accessKey = Read($"{keys.Primary}__ACCESS_KEY", $"{keys.Primary}__ACCESSKEY", keys.LegacyAccessKey);
        var secretKey = Read($"{keys.Primary}__SECRET_KEY", $"{keys.Primary}__SECRETKEY", keys.LegacySecretKey);
        var keyPrefix = Read($"{keys.Primary}__KEY_PREFIX", keys.LegacyKeyPrefix) ?? "";
        var forcePathStyleDefault = keys.LegacyForcePathStyle != null
            ? ReadBool(keys.LegacyForcePathStyle, defaultForcePathStyle)
            : defaultForcePathStyle;
        var forcePathStyle = ReadBool($"{keys.Primary}__FORCE_PATH_STYLE", forcePathStyleDefault);
        var disableChecksum = ReadBool($"{keys.Primary}__DISABLE_CHECKSUM", defaultDisableChecksum);
        var disablePayloadSigning = ReadBool($"{keys.Primary}__DISABLE_PAYLOAD_SIGNING", false);
        var timeoutMinutes = ReadInt($"{keys.Primary}__TIMEOUT_MINUTES", 30, errors);
        var maxRetries = ReadInt($"{keys.Primary}__MAX_RETRIES", 5, errors);

        region = string.IsNullOrWhiteSpace(region) ? defaultRegion : region.Trim();

        if (string.IsNullOrWhiteSpace(endpoint) && !string.IsNullOrWhiteSpace(region) &&
            region != "auto")
        {
            endpoint = $"https://cos.{region}.myqcloud.com";
        }

        if (string.IsNullOrWhiteSpace(endpoint))
        {
            errors.Add($"{label}缺少 {keys.Primary}__ENDPOINT。");
        }
        else if (!Uri.TryCreate(endpoint, UriKind.Absolute, out var endpointUri) ||
                 (endpointUri.Scheme != Uri.UriSchemeHttp && endpointUri.Scheme != Uri.UriSchemeHttps))
        {
            errors.Add($"{label}的 endpoint 不是合法的 HTTP(S) URL：{endpoint}");
        }

        if (string.IsNullOrWhiteSpace(bucket))
        {
            errors.Add($"{label}缺少 {keys.Primary}__BUCKET。");
        }

        if (string.IsNullOrWhiteSpace(region))
        {
            errors.Add($"{label}缺少 {keys.Primary}__REGION。");
        }

        if (string.IsNullOrWhiteSpace(accessKey))
        {
            errors.Add($"{label}缺少 {keys.Primary}__ACCESS_KEY。");
        }

        if (string.IsNullOrWhiteSpace(secretKey))
        {
            errors.Add($"{label}缺少 {keys.Primary}__SECRET_KEY。");
        }

        if (string.IsNullOrWhiteSpace(endpoint) || string.IsNullOrWhiteSpace(bucket))
        {
            return null;
        }

        return new StorageEndpointConfig
        {
            Name = label,
            Endpoint = endpoint.Trim().TrimEnd('/'),
            Bucket = bucket.Trim(),
            Region = region,
            AccessKey = accessKey!.Trim(),
            SecretKey = secretKey!.Trim(),
            KeyPrefix = NormalizePrefix(keyPrefix),
            ForcePathStyle = forcePathStyle,
            DisableChecksum = disableChecksum,
            DisablePayloadSigning = disablePayloadSigning,
            TimeoutMinutes = timeoutMinutes < 1 ? 30 : timeoutMinutes,
            MaxRetries = maxRetries < 0 ? 5 : maxRetries
        };
    }

    private static StorageEndpointConfig CopyEndpoint(StorageEndpointConfig source, string endpoint)
    {
        return new StorageEndpointConfig
        {
            Name = source.Name,
            Endpoint = endpoint,
            Bucket = source.Bucket,
            Region = source.Region,
            AccessKey = source.AccessKey,
            SecretKey = source.SecretKey,
            KeyPrefix = source.KeyPrefix,
            ForcePathStyle = source.ForcePathStyle,
            DisableChecksum = source.DisableChecksum,
            DisablePayloadSigning = source.DisablePayloadSigning,
            TimeoutMinutes = source.TimeoutMinutes,
            MaxRetries = source.MaxRetries
        };
    }

    private static string? ResolveConfigPath(string? explicitPath)
    {
        if (!string.IsNullOrWhiteSpace(explicitPath))
        {
            var fullPath = Path.GetFullPath(explicitPath);
            if (!File.Exists(fullPath))
            {
                throw new ConfigurationException($"配置文件不存在：{fullPath}");
            }

            return fullPath;
        }

        var fromEnvironment = Environment.GetEnvironmentVariable("MIGRATION_CONFIG");
        if (!string.IsNullOrWhiteSpace(fromEnvironment))
        {
            var fullPath = Path.GetFullPath(fromEnvironment);
            if (!File.Exists(fullPath))
            {
                throw new ConfigurationException($"MIGRATION_CONFIG 指向的文件不存在：{fullPath}");
            }

            return fullPath;
        }

        return FindUpwards(Directory.GetCurrentDirectory()) ?? FindUpwards(AppContext.BaseDirectory);
    }

    private static string? FindUpwards(string startDirectory)
    {
        var directory = new DirectoryInfo(startDirectory);
        for (var depth = 0; depth < 8 && directory != null; depth++)
        {
            foreach (var name in DefaultConfigNames)
            {
                var candidate = Path.Combine(directory.FullName, name);
                if (File.Exists(candidate))
                {
                    return candidate;
                }
            }

            directory = directory.Parent;
        }

        return null;
    }

    private static bool IsHttpUrl(string value)
    {
        return Uri.TryCreate(value, UriKind.Absolute, out var uri) &&
               (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps);
    }

    private static string NormalizeDbProvider(string provider, string connectionString)
    {
        if (provider is "postgres" or "postgresql" or "npgsql")
        {
            return "postgres";
        }

        if (provider == "sqlite")
        {
            return "sqlite";
        }

        return connectionString.Contains("Host=", StringComparison.OrdinalIgnoreCase) ||
               connectionString.Contains("Server=", StringComparison.OrdinalIgnoreCase) ||
               connectionString.Contains("Port=", StringComparison.OrdinalIgnoreCase)
            ? "postgres"
            : "sqlite";
    }

    private static string NormalizePrefix(string? prefix)
    {
        return string.IsNullOrWhiteSpace(prefix)
            ? ""
            : prefix.Replace('\\', '/').Trim().Trim('/');
    }

    private static List<string> ReadPublicBases(string key)
    {
        var value = Read(key);
        if (string.IsNullOrWhiteSpace(value))
        {
            return [];
        }

        return value.Split([';', ','], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(x => x.TrimEnd('/'))
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .ToList();
    }

    private static string? Read(params string?[] keys)
    {
        foreach (var key in keys)
        {
            if (string.IsNullOrWhiteSpace(key))
            {
                continue;
            }

            var value = Environment.GetEnvironmentVariable(key);
            if (!string.IsNullOrWhiteSpace(value))
            {
                return value.Trim();
            }
        }

        return null;
    }

    private static bool ReadBool(string key, bool defaultValue)
    {
        var value = Read(key);
        if (string.IsNullOrWhiteSpace(value))
        {
            return defaultValue;
        }

        return value.ToLowerInvariant() switch
        {
            "1" or "true" or "yes" or "on" => true,
            "0" or "false" or "no" or "off" => false,
            _ => defaultValue
        };
    }

    private static int ReadInt(string key, int defaultValue, List<string> errors)
    {
        var value = Read(key);
        if (string.IsNullOrWhiteSpace(value))
        {
            return defaultValue;
        }

        if (int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var result))
        {
            return result;
        }

        errors.Add($"{key} 需要整数，收到：{value}");
        return defaultValue;
    }

    private static VerifyMode ReadVerify(string key, List<string> errors)
    {
        var value = Read(key);
        if (string.IsNullOrWhiteSpace(value))
        {
            return VerifyMode.Size;
        }

        return value.ToLowerInvariant() switch
        {
            "none" or "off" => VerifyMode.None,
            "size" => VerifyMode.Size,
            "sha256" or "sha" => VerifyMode.Sha256,
            _ => AddInvalidVerify(value, errors)
        };
    }

    private static VerifyMode AddInvalidVerify(string value, List<string> errors)
    {
        errors.Add($"{value} 不是合法的校验方式，可选 none/size/sha256。");
        return VerifyMode.Size;
    }

    private static string ResolvePath(string path, string baseDirectory)
    {
        return Path.GetFullPath(Path.IsPathRooted(path) ? path : Path.Combine(baseDirectory, path));
    }

    private sealed record EndpointKeys(
        string Primary,
        string? LegacyPrefix)
    {
        public string? LegacyServiceUrl => LegacyPrefix == null ? null : $"{LegacyPrefix}__ServiceUrl";
        public string? LegacyBucket => LegacyPrefix == null ? null : $"{LegacyPrefix}__Bucket";
        public string? LegacyRegion => LegacyPrefix == null ? null : $"{LegacyPrefix}__Region";
        public string? LegacyAccessKey => LegacyPrefix == null ? null : $"{LegacyPrefix}__AccessKey";
        public string? LegacySecretKey => LegacyPrefix == null ? null : $"{LegacyPrefix}__SecretKey";
        public string? LegacyKeyPrefix => LegacyPrefix == null ? null : $"{LegacyPrefix}__KeyPrefix";
        public string? LegacyForcePathStyle => LegacyPrefix == null ? null : $"{LegacyPrefix}__ForcePathStyle";
    }
}

public sealed class ConfigurationException(string message) : Exception(message);
