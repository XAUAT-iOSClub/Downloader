using System.Security.Cryptography;
using System.Text;

namespace Downloader.Data;

/// <summary>
/// API 密钥的权限范围
/// </summary>
public static class ApiKeyScopes
{
    /// <summary>
    /// 仅允许发布操作：新增应用、上传图标、新增版本、上传安装包
    /// </summary>
    public const string Publish = "publish";
}

/// <summary>
/// API 密钥的生成与校验工具
/// </summary>
public static class ApiKeyTool
{
    /// <summary>
    /// 密钥明文前缀，方便在日志里一眼认出这是本项目签发的密钥
    /// </summary>
    public const string KeyPrefix = "dlk_";

    private const int RandomByteLength = 32;
    private const int DisplayPrefixLength = 12;

    /// <summary>
    /// 生成一个新的密钥明文
    /// </summary>
    public static string CreateKey()
    {
        var bytes = RandomNumberGenerator.GetBytes(RandomByteLength);
        var body = Convert.ToBase64String(bytes)
            .Replace('+', '-')
            .Replace('/', '_')
            .TrimEnd('=');

        return KeyPrefix + body;
    }

    /// <summary>
    /// 计算密钥的 SHA-256 值。
    /// 密钥由 32 字节随机数生成，熵足够高，因此用快速哈希即可，无需 BCrypt。
    /// </summary>
    public static string Hash(string key)
    {
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key))).ToLowerInvariant();
    }

    /// <summary>
    /// 取明文前几位用于展示。传入的既可能是明文，也可能是已存的展示前缀。
    /// </summary>
    public static string GetDisplayPrefix(string key)
    {
        if (key.Length <= DisplayPrefixLength)
        {
            return key;
        }

        return key[..DisplayPrefixLength];
    }

    /// <summary>
    /// 判断明文是否符合本项目的密钥格式
    /// </summary>
    public static bool LooksLikeKey(string key)
    {
        return key.StartsWith(KeyPrefix, StringComparison.Ordinal) && key.Length > KeyPrefix.Length;
    }
}
