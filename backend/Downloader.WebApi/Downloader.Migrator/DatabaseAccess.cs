using Downloader.Data;
using Downloader.Data.Models;
using Microsoft.EntityFrameworkCore;

namespace Downloader.Migrator;

public static class DbKinds
{
    public const string Soft = "soft";
    public const string Icon = "app-icon";
}

public sealed record DbReference(string Kind, string Id, string Name, string? AppId, string OldUrl);

/// <summary>
/// 只负责两件事：读出所有带存储 URL 的记录，以及迁移成功后改写 URL。
/// </summary>
public sealed class DatabaseAccess
{
    private readonly DbContextOptions<DownloaderContext> _options;

    public DatabaseAccess(MigrationConfig config)
    {
        var builder = new DbContextOptionsBuilder<DownloaderContext>();
        if (config.DbProvider == "sqlite")
        {
            builder.UseSqlite(config.ConnectionString);
        }
        else
        {
            builder.UseNpgsql(config.ConnectionString);
        }

        _options = builder.Options;
    }

    public async Task TestConnectionAsync(CancellationToken cancellationToken)
    {
        await using var context = new DownloaderContext(_options);
        if (!await context.Database.CanConnectAsync(cancellationToken))
        {
            throw new InvalidOperationException("无法连接数据库，请检查 SQL/ConnectionStrings__DefaultConnection。");
        }
    }

    public async Task<List<DbReference>> LoadReferencesAsync(MigrationScope scope, string? appId,
        CancellationToken cancellationToken)
    {
        await using var context = new DownloaderContext(_options);
        var references = new List<DbReference>();

        if (scope is MigrationScope.All or MigrationScope.Soft)
        {
            var query = context.SoftModels.AsNoTracking()
                .Where(x => x.SoftUrl != null && x.SoftUrl != "");

            if (!string.IsNullOrWhiteSpace(appId))
            {
                query = query.Where(x => x.Release.AppModelId == appId);
            }

            var softs = await query
                .Select(x => new
                {
                    x.Id,
                    x.Name,
                    x.SoftUrl,
                    AppId = x.Release.AppModelId
                })
                .ToListAsync(cancellationToken);

            references.AddRange(softs.Select(x =>
                new DbReference(DbKinds.Soft, x.Id, x.Name, x.AppId, x.SoftUrl)));
        }

        if (scope is MigrationScope.All or MigrationScope.Icons)
        {
            var query = context.Apps.AsNoTracking()
                .Where(x => x.IconUrl != null && x.IconUrl != "");

            if (!string.IsNullOrWhiteSpace(appId))
            {
                query = query.Where(x => x.Id == appId);
            }

            var apps = await query
                .Select(x => new { x.Id, x.Name, x.IconUrl })
                .ToListAsync(cancellationToken);

            references.AddRange(apps.Select(x =>
                new DbReference(DbKinds.Icon, x.Id, x.Name, x.Id, x.IconUrl)));
        }

        return references;
    }

    public async Task<bool> UpdateUrlAsync(DbReference reference, string newUrl,
        CancellationToken cancellationToken)
    {
        await using var context = new DownloaderContext(_options);
        var rows = reference.Kind switch
        {
            DbKinds.Soft => await context.SoftModels
                .Where(x => x.Id == reference.Id)
                .ExecuteUpdateAsync(setters => setters.SetProperty(x => x.SoftUrl, newUrl), cancellationToken),
            DbKinds.Icon => await context.Apps
                .Where(x => x.Id == reference.Id)
                .ExecuteUpdateAsync(setters => setters.SetProperty(x => x.IconUrl, newUrl), cancellationToken),
            _ => 0
        };

        return rows > 0;
    }
}
