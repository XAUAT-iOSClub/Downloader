namespace Downloader.Data.DTOs;

public class ApiKeyDto
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Prefix { get; set; } = "";
    public string Scope { get; set; } = "";
    public string UserId { get; set; } = "";
    public string Username { get; set; } = "";
    public bool IsActive { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime? ExpiresAt { get; set; }
    public DateTime? LastUsedAt { get; set; }

    /// <summary>
    /// 是否已过期
    /// </summary>
    public bool IsExpired { get; set; }
}

public class ApiKeyCreateDto
{
    public string Name { get; set; } = "";

    /// <summary>
    /// 有效天数。留空或小于等于 0 表示永不过期。
    /// </summary>
    public int? ExpireDays { get; set; }
}

/// <summary>
/// 创建密钥的返回结果。<see cref="Key" /> 是明文，只会返回这一次。
/// </summary>
public class ApiKeyCreatedDto : ApiKeyDto
{
    public string Key { get; set; } = "";
}

public class ApiKeyStatusDto
{
    public bool IsActive { get; set; }
}
