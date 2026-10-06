namespace Downloader.WebApi.Auth;

public static class AuthSchemeNames
{
    /// <summary>
    /// 策略方案：按请求中是否带 X-API-Key 请求头，在 JWT 与 API Key 之间自动转发
    /// </summary>
    public const string Smart = "Smart";
}

public static class AuthorizationPolicies
{
    /// <summary>
    /// 发布权限：Admin 的 JWT，或持有 publish 范围的 API Key。
    /// 只挂在「新增应用 / 上传图标 / 新增版本 / 上传安装包」这几个接口上。
    /// </summary>
    public const string Publish = "Publish";

    /// <summary>
    /// 仅限 JWT 登录的 Admin。用于密钥管理本身，避免 API Key 自行签发新密钥。
    /// </summary>
    public const string JwtAdmin = "JwtAdmin";
}
