namespace Downloader.Migrator;

public enum KeyMappingStatus
{
    /// <summary>能从旧 URL/对象键解析出源键和目标键。</summary>
    Resolved,

    /// <summary>URL 已经指向目标存储，不需要再从源端复制。</summary>
    AlreadyMigrated,

    /// <summary>空 URL。</summary>
    Empty,

    /// <summary>对象键不在源 KeyPrefix 下，可能是手工上传或历史遗留。</summary>
    OutsideSourcePrefix,

    /// <summary>不是可识别的对象存储 URL。</summary>
    Unresolved
}

public sealed record KeyMapping(
    KeyMappingStatus Status,
    string SourceKey,
    string TargetKey,
    string NewUrl,
    bool NeedsCopy,
    bool CanDeleteSource,
    string Message)
{
    public bool IsUsable => Status is KeyMappingStatus.Resolved or KeyMappingStatus.AlreadyMigrated;

    public bool UrlChanged(string originalUrl) =>
        !string.Equals(originalUrl.Trim(), NewUrl, StringComparison.Ordinal);
}

/// <summary>
/// 把数据库里的公开 URL 还原为对象键，并按 KeyPrefix 映射到目标存储。
/// 支持应用当前的代理 URL（https://host/files/{key}）、S3/COS/R2 直链和裸对象键。
/// </summary>
public sealed class StorageKeyMapper
{
    private readonly MigrationBehavior _behavior;
    private readonly StorageEndpointConfig _source;
    private readonly StorageEndpointConfig _target;

    public StorageKeyMapper(MigrationBehavior behavior, StorageEndpointConfig source, StorageEndpointConfig target)
    {
        _behavior = behavior;
        _source = source;
        _target = target;
    }

    public KeyMapping Map(string? url)
    {
        var raw = (url ?? "").Trim();
        if (raw.Length == 0)
        {
            return Fail(KeyMappingStatus.Empty, raw, "URL 为空。");
        }

        if (Uri.TryCreate(raw, UriKind.Absolute, out var uri) &&
            (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps))
        {
            foreach (var baseUrl in _behavior.OldPublicBases)
            {
                if (TryStripBase(uri, baseUrl, out var escapedKey))
                {
                    return ResolveKey(raw, DecodePath(escapedKey), needsCopy: true);
                }
            }

            foreach (var baseUrl in _behavior.TargetPublicBases)
            {
                if (TryStripBase(uri, baseUrl, out var escapedKey))
                {
                    return ResolveKey(raw, DecodePath(escapedKey), needsCopy: false,
                        preferTargetPrefix: true);
                }
            }

            if (TryResolveDirectUri(uri, out var directKey, out var directIsTarget))
            {
                return ResolveKey(raw, directKey, needsCopy: !directIsTarget, preferTargetPrefix: directIsTarget);
            }

            return Fail(KeyMappingStatus.Unresolved, raw,
                "URL 不属于配置的旧/新公开前缀，也不是已知的存储端点直链。");
        }

        if (raw.Contains("://", StringComparison.Ordinal))
        {
            return Fail(KeyMappingStatus.Unresolved, raw, "不支持的 URL scheme。");
        }

        var key = DecodePath(raw);
        return ResolveKey(raw, key, needsCopy: true);
    }

    private KeyMapping ResolveKey(string originalUrl, string key, bool needsCopy,
        bool preferTargetPrefix = false)
    {
        key = NormalizeKey(key);
        if (key.Length == 0)
        {
            return Fail(KeyMappingStatus.Empty, originalUrl, "URL 中没有对象键。");
        }

        var sourcePrefix = NormalizePrefix(_source.KeyPrefix);
        var targetPrefix = NormalizePrefix(_target.KeyPrefix);
        var alreadyTarget = !needsCopy || preferTargetPrefix;

        string relative;
        if (alreadyTarget)
        {
            relative = StripPrefix(key, targetPrefix) ?? key;
        }
        else if (sourcePrefix.Length > 0)
        {
            var stripped = StripPrefix(key, sourcePrefix);
            if (stripped != null)
            {
                relative = stripped;
            }
            else
            {
                // 前缀被改过时，已经迁移过的记录需要按目标前缀识别，保证可重复执行。
                var targetStripped = targetPrefix.Length > 0 && targetPrefix != sourcePrefix
                    ? StripPrefix(key, targetPrefix)
                    : null;
                if (targetStripped != null)
                {
                    relative = targetStripped;
                    alreadyTarget = true;
                }
                else if (key.StartsWith("uploads/", StringComparison.Ordinal))
                {
                    relative = key;
                }
                else
                {
                    return Fail(KeyMappingStatus.OutsideSourcePrefix, originalUrl,
                        $"对象键 {key} 不在源 KeyPrefix '{sourcePrefix}' 下，也未以 uploads/ 开头。");
                }
            }
        }
        else
        {
            relative = key;
        }

        if (relative.Length == 0)
        {
            return Fail(KeyMappingStatus.OutsideSourcePrefix, originalUrl,
                $"对象键 {key} 只有 KeyPrefix，没有实际文件路径。");
        }

        // 已经指向目标的记录保留原始键，避免重复执行时再次拼接前缀。
        var targetKey = alreadyTarget
            ? key
            : targetPrefix.Length == 0
                ? relative
                : $"{targetPrefix}/{relative}";
        var sourceKey = key;
        var canDeleteSource = true;

        if (alreadyTarget)
        {
            // 从目标键反推源键，用于数据库已改写后仍能执行源端清理。
            // 如果目标键不符合目标前缀，无法安全反推，则禁止删源。
            var targetRelative = targetPrefix.Length > 0 ? StripPrefix(key, targetPrefix) : key;
            if (targetRelative == null)
            {
                sourceKey = key;
                canDeleteSource = false;
            }
            else
            {
                sourceKey = sourcePrefix.Length == 0
                    ? targetRelative
                    : $"{sourcePrefix}/{targetRelative}";
            }
        }

        var newUrl = BuildPublicUrl(targetKey);

        var status = alreadyTarget ? KeyMappingStatus.AlreadyMigrated : KeyMappingStatus.Resolved;
        var message = alreadyTarget
            ? "已指向目标存储或目标 KeyPrefix。"
            : $"源键 {key} -> 目标键 {targetKey}";

        return new KeyMapping(status, sourceKey, targetKey, newUrl, needsCopy && !alreadyTarget,
            canDeleteSource, message);
    }

    private bool TryResolveDirectUri(Uri uri, out string key, out bool isTarget)
    {
        key = "";
        isTarget = false;

        var host = uri.Host;
        var pathSegments = uri.AbsolutePath
            .Split('/', StringSplitOptions.RemoveEmptyEntries)
            .Select(Uri.UnescapeDataString)
            .ToList();

        if (MatchesEndpointHost(host, _source, out var sourcePathStyle))
        {
            if (sourcePathStyle && pathSegments.Count > 0 && pathSegments[0] == _source.Bucket)
            {
                pathSegments.RemoveAt(0);
            }

            key = string.Join('/', pathSegments);
            return key.Length > 0;
        }

        if (MatchesEndpointHost(host, _target, out var targetPathStyle))
        {
            if (targetPathStyle && pathSegments.Count > 0 && pathSegments[0] == _target.Bucket)
            {
                pathSegments.RemoveAt(0);
            }

            key = string.Join('/', pathSegments);
            isTarget = true;
            return key.Length > 0;
        }

        return false;
    }

    private static bool MatchesEndpointHost(string host, StorageEndpointConfig endpoint, out bool pathStyle)
    {
        pathStyle = true;
        var endpointHost = endpoint.EndpointHost;

        if (string.Equals(host, endpointHost, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var virtualHostPrefix = $"{endpoint.Bucket}.{endpointHost}";
        if (string.Equals(host, virtualHostPrefix, StringComparison.OrdinalIgnoreCase))
        {
            pathStyle = false;
            return true;
        }

        return false;
    }

    private static bool TryStripBase(Uri uri, string baseUrl, out string escapedKey)
    {
        escapedKey = "";

        if (!Uri.TryCreate(baseUrl, UriKind.Absolute, out var baseUri) ||
            !string.Equals(uri.Scheme, baseUri.Scheme, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(uri.Host, baseUri.Host, StringComparison.OrdinalIgnoreCase) ||
            uri.Port != baseUri.Port)
        {
            return false;
        }

        var basePath = baseUri.AbsolutePath.TrimEnd('/');
        var path = uri.AbsolutePath;

        if (basePath.Length == 0)
        {
            escapedKey = path.Trim('/');
            return true;
        }

        if (!path.StartsWith(basePath, StringComparison.Ordinal))
        {
            return false;
        }

        if (path.Length > basePath.Length && path[basePath.Length] != '/')
        {
            return false;
        }

        escapedKey = path.Length > basePath.Length ? path[basePath.Length..].Trim('/') : "";
        return true;
    }

    private string BuildPublicUrl(string key)
    {
        var baseUrl = _behavior.NewPublicBaseUrl.TrimEnd('/');
        if (key.Length == 0)
        {
            return baseUrl;
        }

        var escaped = string.Join('/', key.Split('/').Select(Uri.EscapeDataString));
        return $"{baseUrl}/{escaped}";
    }

    private static string DecodePath(string escapedPath)
    {
        return string.Join('/',
            escapedPath.Split('/', StringSplitOptions.RemoveEmptyEntries)
                .Select(Uri.UnescapeDataString));
    }

    private static string NormalizeKey(string key)
    {
        return key.Replace('\\', '/').Trim().Trim('/');
    }

    private static string NormalizePrefix(string prefix)
    {
        return string.IsNullOrWhiteSpace(prefix) ? "" : prefix.Replace('\\', '/').Trim().Trim('/');
    }

    private static string? StripPrefix(string key, string prefix)
    {
        if (prefix.Length == 0)
        {
            return key;
        }

        if (key.Equals(prefix, StringComparison.Ordinal))
        {
            return "";
        }

        return key.StartsWith($"{prefix}/", StringComparison.Ordinal)
            ? key[(prefix.Length + 1)..]
            : null;
    }

    private static KeyMapping Fail(KeyMappingStatus status, string url, string message)
    {
        return new KeyMapping(status, "", "", url, false, false, message);
    }
}
