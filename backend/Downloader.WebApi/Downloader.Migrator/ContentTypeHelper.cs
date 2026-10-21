namespace Downloader.Migrator;

internal static class ContentTypeHelper
{
    private static readonly Dictionary<string, string> ContentTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        [".apk"] = "application/vnd.android.package-archive",
        [".ipa"] = "application/octet-stream",
        [".msix"] = "application/msix",
        [".exe"] = "application/vnd.microsoft.portable-executable",
        [".dmg"] = "application/x-apple-diskimage",
        [".pkg"] = "application/octet-stream",
        [".zip"] = "application/zip",
        [".7z"] = "application/x-7z-compressed",
        [".tar"] = "application/x-tar",
        [".gz"] = "application/gzip",
        [".png"] = "image/png",
        [".jpg"] = "image/jpeg",
        [".jpeg"] = "image/jpeg",
        [".gif"] = "image/gif",
        [".webp"] = "image/webp",
        [".svg"] = "image/svg+xml",
        [".txt"] = "text/plain",
        [".json"] = "application/json"
    };

    public static string Get(string path)
    {
        var extension = Path.GetExtension(path);
        return !string.IsNullOrWhiteSpace(extension) && ContentTypes.TryGetValue(extension, out var contentType)
            ? contentType
            : "application/octet-stream";
    }
}
