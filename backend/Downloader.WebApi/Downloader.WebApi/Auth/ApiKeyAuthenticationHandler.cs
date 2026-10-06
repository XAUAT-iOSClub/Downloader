using System.Security.Claims;
using System.Text.Encodings.Web;
using Downloader.DataApi.Services;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;

namespace Downloader.WebApi.Auth;

public static class ApiKeyAuthDefaults
{
    public const string SchemeName = "ApiKey";

    /// <summary>
    /// 携带密钥明文的请求头
    /// </summary>
    public const string HeaderName = "X-API-Key";

    public const string ScopeClaimType = "scope";
    public const string ApiKeyIdClaimType = "api_key_id";

    /// <summary>
    /// API Key 认证通过后写入的角色。刻意不写入用户自身的角色（如 Admin），
    /// 这样管理类接口上的 [Authorize(Roles = "Admin")] 会自然拒绝 API Key，
    /// 只有标注了 Publish 策略的发布接口才放行。
    /// </summary>
    public const string RoleName = "ApiKey";
}

public sealed class ApiKeyAuthenticationOptions : AuthenticationSchemeOptions;

public class ApiKeyAuthenticationHandler(
    IOptionsMonitor<ApiKeyAuthenticationOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder,
    IApiKeyService apiKeyService)
    : AuthenticationHandler<ApiKeyAuthenticationOptions>(options, logger, encoder)
{
    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (!Request.Headers.TryGetValue(ApiKeyAuthDefaults.HeaderName, out var headerValues))
        {
            return AuthenticateResult.NoResult();
        }

        var plaintextKey = headerValues.ToString().Trim();
        if (string.IsNullOrEmpty(plaintextKey))
        {
            return AuthenticateResult.Fail("请求头 X-API-Key 为空。");
        }

        var apiKey = await apiKeyService.AuthenticateAsync(plaintextKey);
        if (apiKey?.User == null)
        {
            return AuthenticateResult.Fail("API Key 无效、已停用或已过期。");
        }

        var identity = new ClaimsIdentity(
        [
            new Claim(ClaimTypes.NameIdentifier, apiKey.UserId),
            new Claim(ClaimTypes.Name, apiKey.User.Username),
            new Claim(ClaimTypes.Email, apiKey.User.Email),
            new Claim(ClaimTypes.Role, ApiKeyAuthDefaults.RoleName),
            new Claim(ApiKeyAuthDefaults.ScopeClaimType, apiKey.Scope),
            new Claim(ApiKeyAuthDefaults.ApiKeyIdClaimType, apiKey.Id)
        ], Scheme.Name);

        return AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), Scheme.Name));
    }
}
