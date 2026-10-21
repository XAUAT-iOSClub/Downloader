using Downloader.Data;
using Downloader.Data.Models;
using Downloader.Migrator;
using Microsoft.EntityFrameworkCore;

namespace Downloader.Test;

[TestFixture]
public class DatabaseAccessTests
{
    private string _databasePath = "";

    [SetUp]
    public void SetUp()
    {
        _databasePath = Path.Combine(Path.GetTempPath(), $"downloader-migrator-test-{Guid.NewGuid():N}.db");
    }

    [TearDown]
    public void TearDown()
    {
        if (File.Exists(_databasePath))
        {
            File.Delete(_databasePath);
        }
    }

    [Test]
    public async Task LoadsSoftAndIconUrlsAndUpdatesThem()
    {
        var connectionString = $"Data Source={_databasePath}";
        var options = new DbContextOptionsBuilder<DownloaderContext>()
            .UseSqlite(connectionString)
            .Options;

        await using (var context = new DownloaderContext(options))
        {
            await context.Database.EnsureCreatedAsync();

            var user = new UserModel
            {
                Id = "user-1",
                Username = "tester",
                Password = "hash",
                Email = "tester@example.com",
                Identity = "Admin"
            };
            var channel = new ChannelModel { Id = "channel-1", Name = "Windows" };
            var app = new AppModel
            {
                Id = "app-1",
                Name = "Demo",
                Description = "Demo app",
                IconUrl = "https://appapi.xauat.site/files/downloader/uploads/app-icons/icon.png",
                UserId = user.Id,
                User = user
            };
            var release = new ReleaseModel
            {
                Id = "release-1",
                Name = "1.0.0",
                ReleaseId = "1.0.0",
                AppModelId = app.Id,
                AppModel = app,
                ReleaseDate = DateTime.UtcNow
            };
            var soft = new SoftModel
            {
                Id = "soft-1",
                Name = "installer",
                SoftUrl = "https://appapi.xauat.site/files/downloader/uploads/software/app.apk",
                ReleaseId = release.Id,
                Release = release,
                ChannelId = channel.Id,
                Channel = channel
            };

            context.Users.Add(user);
            context.Channels.Add(channel);
            context.Apps.Add(app);
            context.Releases.Add(release);
            context.SoftModels.Add(soft);
            await context.SaveChangesAsync();
        }

        var access = new DatabaseAccess(CreateConfig(connectionString));

        var references = await access.LoadReferencesAsync(MigrationScope.All, null, CancellationToken.None);
        Assert.That(references, Has.Count.EqualTo(2));
        Assert.That(references.Single(x => x.Kind == DbKinds.Soft).OldUrl,
            Does.Contain("uploads/software/app.apk"));
        Assert.That(references.Single(x => x.Kind == DbKinds.Icon).OldUrl,
            Does.Contain("uploads/app-icons/icon.png"));

        var icon = references.Single(x => x.Kind == DbKinds.Icon);
        var updated = await access.UpdateUrlAsync(icon, "https://new.example.com/icon.png",
            CancellationToken.None);
        Assert.That(updated, Is.True);

        var after = await access.LoadReferencesAsync(MigrationScope.Icons, null, CancellationToken.None);
        Assert.That(after.Single().OldUrl, Is.EqualTo("https://new.example.com/icon.png"));
    }

    private static MigrationConfig CreateConfig(string connectionString)
    {
        return new MigrationConfig
        {
            ConnectionString = connectionString,
            DbProvider = "sqlite",
            Source = new StorageEndpointConfig
            {
                Name = "source",
                Endpoint = "https://source.example.com",
                Bucket = "source",
                Region = "auto",
                AccessKey = "key",
                SecretKey = "secret"
            },
            Target = new StorageEndpointConfig
            {
                Name = "target",
                Endpoint = "https://target.example.com",
                Bucket = "target",
                Region = "region",
                AccessKey = "key",
                SecretKey = "secret"
            },
            Behavior = new MigrationBehavior
            {
                OldPublicBases = ["https://appapi.xauat.site/files"],
                TargetPublicBases = ["https://appapi.xauat.site/files"],
                NewPublicBaseUrl = "https://appapi.xauat.site/files",
                StateFile = "state.json",
                ReportFile = "report.csv",
                LogFile = "migration.log"
            }
        };
    }
}
