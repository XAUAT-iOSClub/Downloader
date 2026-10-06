using Downloader.Data;
using Downloader.Data.DTOs;
using Downloader.Data.Models;
using Downloader.DataApi.Repos;

namespace Downloader.DataApi.Services;

public interface IApiKeyService
{
    Task<List<ApiKeyDto>> GetAllAsync();
    Task<ApiKeyCreatedDto?> CreateAsync(ApiKeyCreateDto dto, string userId);
    Task<bool> SetActiveAsync(string id, bool isActive);
    Task<bool> DeleteAsync(string id);

    /// <summary>
    /// 校验密钥明文。通过时返回密钥实体（含所属用户），否则返回 null。
    /// </summary>
    Task<ApiKeyModel?> AuthenticateAsync(string plaintextKey);
}

public class ApiKeyService(IApiKeyRepo apiKeyRepo, IUserRepo userRepo) : IApiKeyService
{
    public async Task<List<ApiKeyDto>> GetAllAsync()
    {
        var apiKeys = await apiKeyRepo.GetAllAsync();
        return apiKeys.Select(ToDto).ToList();
    }

    public async Task<ApiKeyCreatedDto?> CreateAsync(ApiKeyCreateDto dto, string userId)
    {
        var user = await userRepo.GetByIdAsync(userId);
        if (user == null) return null;

        var plaintextKey = ApiKeyTool.CreateKey();
        var model = new ApiKeyModel
        {
            Name = string.IsNullOrWhiteSpace(dto.Name) ? "未命名密钥" : dto.Name.Trim(),
            KeyHash = ApiKeyTool.Hash(plaintextKey),
            Prefix = ApiKeyTool.GetDisplayPrefix(plaintextKey),
            UserId = user.Id,
            Scope = ApiKeyScopes.Publish,
            IsActive = true,
            CreatedAt = DateTime.UtcNow,
            ExpiresAt = dto.ExpireDays is > 0 ? DateTime.UtcNow.AddDays(dto.ExpireDays.Value) : null
        };

        var created = await apiKeyRepo.CreateAsync(model);

        var result = new ApiKeyCreatedDto { Key = plaintextKey };
        CopyTo(created, result);
        result.Username = user.Username;
        return result;
    }

    public async Task<bool> SetActiveAsync(string id, bool isActive)
    {
        var apiKey = await apiKeyRepo.GetByIdAsync(id);
        if (apiKey == null) return false;

        apiKey.IsActive = isActive;
        return await apiKeyRepo.UpdateAsync(apiKey);
    }

    public async Task<bool> DeleteAsync(string id)
    {
        return await apiKeyRepo.DeleteAsync(id);
    }

    public async Task<ApiKeyModel?> AuthenticateAsync(string plaintextKey)
    {
        if (!ApiKeyTool.LooksLikeKey(plaintextKey)) return null;

        var apiKey = await apiKeyRepo.GetByHashAsync(ApiKeyTool.Hash(plaintextKey));

        // 校验失效、过期两种情况一律视为无效密钥
        if (apiKey == null || !apiKey.IsActive) return null;
        if (apiKey.ExpiresAt is { } expiresAt && expiresAt <= DateTime.UtcNow) return null;

        await apiKeyRepo.TouchLastUsedAsync(apiKey.Id, DateTime.UtcNow);

        return apiKey;
    }

    private static ApiKeyDto ToDto(ApiKeyModel model)
    {
        var dto = new ApiKeyDto { Username = model.User?.Username ?? "" };
        CopyTo(model, dto);
        return dto;
    }

    private static void CopyTo(ApiKeyModel model, ApiKeyDto dto)
    {
        dto.Id = model.Id;
        dto.Name = model.Name;
        dto.Prefix = model.Prefix;
        dto.Scope = model.Scope;
        dto.UserId = model.UserId;
        dto.IsActive = model.IsActive;
        dto.CreatedAt = model.CreatedAt;
        dto.ExpiresAt = model.ExpiresAt;
        dto.LastUsedAt = model.LastUsedAt;
        dto.IsExpired = model.ExpiresAt is { } expiresAt && expiresAt <= DateTime.UtcNow;
    }
}
