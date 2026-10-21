using Downloader.Migrator;

namespace Downloader.Test;

[TestFixture]
public class StorageKeyMapperTests
{
    private static StorageKeyMapper CreateMapper(
        string sourcePrefix = "downloader",
        string targetPrefix = "downloader",
        string oldBase = "https://appapi.xauat.site/files",
        string newBase = "https://appapi.xauat.site/files")
    {
        var behavior = new MigrationBehavior
        {
            OldPublicBases = [oldBase],
            TargetPublicBases = [newBase],
            NewPublicBaseUrl = newBase,
            StateFile = "state.json",
            ReportFile = "report.csv",
            LogFile = "migration.log"
        };

        return new StorageKeyMapper(
            behavior,
            new StorageEndpointConfig
            {
                Name = "source",
                Endpoint = "https://account.r2.cloudflarestorage.com",
                Bucket = "app",
                Region = "auto",
                AccessKey = "key",
                SecretKey = "secret",
                KeyPrefix = sourcePrefix,
                ForcePathStyle = true
            },
            new StorageEndpointConfig
            {
                Name = "target",
                Endpoint = "https://cos.ap-guangzhou.myqcloud.com",
                Bucket = "app-1250000000",
                Region = "ap-guangzhou",
                AccessKey = "key",
                SecretKey = "secret",
                KeyPrefix = targetPrefix
            });
    }

    [Test]
    public void MapsProxyUrlAndPreservesKey()
    {
        var mapper = CreateMapper();
        var mapping = mapper.Map(
            "https://appapi.xauat.site/files/downloader/uploads/software/20260101-a%20b.apk");

        Assert.That(mapping.Status, Is.EqualTo(KeyMappingStatus.Resolved));
        Assert.That(mapping.SourceKey, Is.EqualTo("downloader/uploads/software/20260101-a b.apk"));
        Assert.That(mapping.TargetKey, Is.EqualTo("downloader/uploads/software/20260101-a b.apk"));
        Assert.That(mapping.NewUrl,
            Is.EqualTo("https://appapi.xauat.site/files/downloader/uploads/software/20260101-a%20b.apk"));
        Assert.That(mapping.NeedsCopy, Is.True);
        Assert.That(mapping.UrlChanged(
            "https://appapi.xauat.site/files/downloader/uploads/software/20260101-a%20b.apk"), Is.False);
    }

    [Test]
    public void RewritesUrlWhenTargetPrefixDiffers()
    {
        var mapper = CreateMapper(targetPrefix: "dl");
        var mapping = mapper.Map(
            "https://appapi.xauat.site/files/downloader/uploads/app-icons/icon.png");

        Assert.That(mapping.Status, Is.EqualTo(KeyMappingStatus.Resolved));
        Assert.That(mapping.TargetKey, Is.EqualTo("dl/uploads/app-icons/icon.png"));
        Assert.That(mapping.NewUrl,
            Is.EqualTo("https://appapi.xauat.site/files/dl/uploads/app-icons/icon.png"));
        Assert.That(mapping.UrlChanged(
            "https://appapi.xauat.site/files/downloader/uploads/app-icons/icon.png"), Is.True);
    }

    [Test]
    public void RecognizesAlreadyMigratedTargetUrlAndDerivesSourceKey()
    {
        var mapper = CreateMapper(
            targetPrefix: "dl",
            oldBase: "https://appapi.xauat.site/files",
            newBase: "https://cdn.example.com/files");
        var mapping = mapper.Map("https://cdn.example.com/files/dl/uploads/software/app.apk");

        Assert.That(mapping.Status, Is.EqualTo(KeyMappingStatus.AlreadyMigrated));
        Assert.That(mapping.NeedsCopy, Is.False);
        Assert.That(mapping.SourceKey, Is.EqualTo("downloader/uploads/software/app.apk"));
        Assert.That(mapping.TargetKey, Is.EqualTo("dl/uploads/software/app.apk"));
        Assert.That(mapping.CanDeleteSource, Is.True);
    }

    [Test]
    public void DoesNotDeleteSourceWhenTargetKeyCannotBeReversed()
    {
        var mapper = CreateMapper(
            targetPrefix: "downloader",
            oldBase: "https://appapi.xauat.site/files",
            newBase: "https://cdn.example.com/files");
        var mapping = mapper.Map("https://cdn.example.com/files/dl/uploads/software/app.apk");

        Assert.That(mapping.Status, Is.EqualTo(KeyMappingStatus.AlreadyMigrated));
        Assert.That(mapping.CanDeleteSource, Is.False);
    }

    [Test]
    public void RecognizesBareStorageKey()
    {
        var mapper = CreateMapper();
        var mapping = mapper.Map("downloader/uploads/software/app.apk");

        Assert.That(mapping.Status, Is.EqualTo(KeyMappingStatus.Resolved));
        Assert.That(mapping.TargetKey, Is.EqualTo("downloader/uploads/software/app.apk"));
    }

    [Test]
    public void RecognizesPathStyleR2Url()
    {
        var mapper = CreateMapper();
        var mapping = mapper.Map(
            "https://account.r2.cloudflarestorage.com/app/downloader/uploads/software/app.apk");

        Assert.That(mapping.Status, Is.EqualTo(KeyMappingStatus.Resolved));
        Assert.That(mapping.SourceKey, Is.EqualTo("downloader/uploads/software/app.apk"));
    }

    [Test]
    public void RecognizesVirtualHostedCosUrlAsTarget()
    {
        var mapper = CreateMapper();
        var mapping = mapper.Map(
            "https://app-1250000000.cos.ap-guangzhou.myqcloud.com/downloader/uploads/software/app.apk");

        Assert.That(mapping.Status, Is.EqualTo(KeyMappingStatus.AlreadyMigrated));
        Assert.That(mapping.NeedsCopy, Is.False);
    }

    [Test]
    public void RejectsExternalUrl()
    {
        var mapper = CreateMapper();
        var mapping = mapper.Map("https://example.com/downloads/app.apk");

        Assert.That(mapping.Status, Is.EqualTo(KeyMappingStatus.Unresolved));
        Assert.That(mapping.IsUsable, Is.False);
    }

    [Test]
    public void RejectsKeyOutsideSourcePrefix()
    {
        var mapper = CreateMapper();
        var mapping = mapper.Map(
            "https://appapi.xauat.site/files/other/uploads/software/app.apk");

        Assert.That(mapping.Status, Is.EqualTo(KeyMappingStatus.OutsideSourcePrefix));
    }

    [Test]
    public void HandlesEmptyUrl()
    {
        var mapper = CreateMapper();
        var mapping = mapper.Map("  ");

        Assert.That(mapping.Status, Is.EqualTo(KeyMappingStatus.Empty));
    }
}
