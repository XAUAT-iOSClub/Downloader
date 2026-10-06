using Downloader.Data;
using Downloader.Data.Models;
using Microsoft.EntityFrameworkCore;

namespace Downloader.DataApi.Repos;

public interface IApiKeyRepo
{
    Task<List<ApiKeyModel>> GetAllAsync();
    Task<ApiKeyModel?> GetByIdAsync(string id);
    Task<ApiKeyModel?> GetByHashAsync(string keyHash);
    Task<ApiKeyModel> CreateAsync(ApiKeyModel apiKey);
    Task<bool> UpdateAsync(ApiKeyModel apiKey);
    Task<bool> DeleteAsync(string id);
    Task TouchLastUsedAsync(string id, DateTime usedAt);
}

public class ApiKeyRepo(IDbContextFactory<DownloaderContext> contextFactory) : IApiKeyRepo
{
    public async Task<List<ApiKeyModel>> GetAllAsync()
    {
        await using var context = await contextFactory.CreateDbContextAsync();
        return await context.ApiKeys
            .Include(k => k.User)
            .OrderByDescending(k => k.CreatedAt)
            .ToListAsync();
    }

    public async Task<ApiKeyModel?> GetByIdAsync(string id)
    {
        await using var context = await contextFactory.CreateDbContextAsync();
        return await context.ApiKeys
            .Include(k => k.User)
            .FirstOrDefaultAsync(k => k.Id == id);
    }

    public async Task<ApiKeyModel?> GetByHashAsync(string keyHash)
    {
        await using var context = await contextFactory.CreateDbContextAsync();
        return await context.ApiKeys
            .Include(k => k.User)
            .FirstOrDefaultAsync(k => k.KeyHash == keyHash);
    }

    public async Task<ApiKeyModel> CreateAsync(ApiKeyModel apiKey)
    {
        await using var context = await contextFactory.CreateDbContextAsync();
        context.ApiKeys.Add(apiKey);
        await context.SaveChangesAsync();
        return apiKey;
    }

    public async Task<bool> UpdateAsync(ApiKeyModel apiKey)
    {
        await using var context = await contextFactory.CreateDbContextAsync();
        context.ApiKeys.Update(apiKey);
        await context.SaveChangesAsync();
        return true;
    }

    public async Task<bool> DeleteAsync(string id)
    {
        await using var context = await contextFactory.CreateDbContextAsync();
        var apiKey = await context.ApiKeys.FindAsync(id);
        if (apiKey == null) return false;

        context.ApiKeys.Remove(apiKey);
        await context.SaveChangesAsync();
        return true;
    }

    public async Task TouchLastUsedAsync(string id, DateTime usedAt)
    {
        await using var context = await contextFactory.CreateDbContextAsync();
        await context.ApiKeys
            .Where(k => k.Id == id)
            .ExecuteUpdateAsync(setters => setters.SetProperty(k => k.LastUsedAt, usedAt));
    }
}
