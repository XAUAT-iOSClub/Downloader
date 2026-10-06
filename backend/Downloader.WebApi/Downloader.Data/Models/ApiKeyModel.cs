using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Downloader.Data.Models;

/// <summary>
/// API 密钥。用于发布流程（新增版本、上传应用）的免登录凭据，
/// 只授权发布操作，不具备管理后台的完整权限。
/// </summary>
[Table("api_keys")]
public class ApiKeyModel
{
    [Key] [MaxLength(64)] public string Id { get; set; } = Guid.NewGuid().ToString();

    /// <summary>
    /// 便于人识别的备注名，例如「CI 发布机」
    /// </summary>
    [MaxLength(64)]
    public string Name { get; set; } = "";

    /// <summary>
    /// 密钥明文的 SHA-256 值。明文只在创建时返回一次，不落库。
    /// </summary>
    [MaxLength(64)]
    public string KeyHash { get; set; } = "";

    /// <summary>
    /// 明文前几位，用于在列表中辨认是哪一个密钥
    /// </summary>
    [MaxLength(32)]
    public string Prefix { get; set; } = "";

    [MaxLength(64)] public string UserId { get; set; } = "";

    public UserModel User { get; set; } = null!;

    /// <summary>
    /// 权限范围，见 <see cref="ApiKeyScopes" />
    /// </summary>
    [MaxLength(32)]
    public string Scope { get; set; } = ApiKeyScopes.Publish;

    public bool IsActive { get; set; } = true;

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// 过期时间，null 表示永不过期
    /// </summary>
    public DateTime? ExpiresAt { get; set; }

    /// <summary>
    /// 最后一次通过校验的时间
    /// </summary>
    public DateTime? LastUsedAt { get; set; }
}
