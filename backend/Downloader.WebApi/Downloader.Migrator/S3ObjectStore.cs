using System.Net;
using System.Security.Cryptography;
using Amazon.Runtime;
using Amazon.S3;
using Amazon.S3.Model;

namespace Downloader.Migrator;

public sealed record ObjectMetadata(long Length, string? ContentType, string? CacheControl, string? ETag);

public sealed class SourceObject(GetObjectResponse response, ObjectMetadata metadata) : IAsyncDisposable
{
    public Stream Stream { get; } = response.ResponseStream;
    public ObjectMetadata Metadata { get; } = metadata;

    public ValueTask DisposeAsync()
    {
        response.Dispose();
        return ValueTask.CompletedTask;
    }
}

/// <summary>
/// 对 S3 兼容存储的最小封装。R2 与 COS 都通过它读写。
/// </summary>
public sealed class S3ObjectStore : IDisposable
{
    private const long MinimumPartSize = 5L * 1024 * 1024;
    private const int MaximumPartCount = 10_000;

    private readonly StorageEndpointConfig _config;
    private readonly AmazonS3Client _client;
    private readonly long _partSize;
    private readonly long _smallObjectThreshold;

    public S3ObjectStore(StorageEndpointConfig config, long partSize, long smallObjectThreshold)
    {
        _config = config;
        _partSize = Math.Max(MinimumPartSize, partSize);
        _smallObjectThreshold = Math.Max(0, smallObjectThreshold);
        _client = CreateClient(config);
    }

    public string Name => _config.Name;
    public string Bucket => _config.Bucket;
    public string KeyPrefix => _config.KeyPrefix;

    public async Task<ObjectMetadata?> HeadAsync(string key, CancellationToken cancellationToken)
    {
        try
        {
            var response = await _client.GetObjectMetadataAsync(_config.Bucket, key, cancellationToken);
            return ToMetadata(response);
        }
        catch (AmazonS3Exception exception) when (IsNotFound(exception))
        {
            return null;
        }
    }

    public async Task<SourceObject?> OpenReadAsync(string key, CancellationToken cancellationToken)
    {
        try
        {
            var response = await _client.GetObjectAsync(_config.Bucket, key, cancellationToken);
            var metadata = ToMetadata(response);
            return new SourceObject(response, metadata);
        }
        catch (AmazonS3Exception exception) when (IsNotFound(exception))
        {
            return null;
        }
    }

    public async Task UploadAsync(string key, Stream source, long length, string? contentType,
        string? cacheControl, CancellationToken cancellationToken)
    {
        if (length < 0)
        {
            throw new InvalidOperationException("无法确定源对象大小，拒绝上传。");
        }

        if (length == 0)
        {
            await PutSmallObjectAsync(key, [], contentType, cacheControl, cancellationToken);
            return;
        }

        if (length <= _smallObjectThreshold)
        {
            var buffer = GC.AllocateUninitializedArray<byte>((int)length);
            var read = await ReadAtMostAsync(source, buffer, cancellationToken);
            if (read != length)
            {
                throw new IOException($"源对象在传输中提前结束：期望 {length} 字节，实际 {read} 字节。");
            }

            await PutSmallObjectAsync(key, buffer, contentType, cacheControl, cancellationToken);
            return;
        }

        await MultipartUploadAsync(key, source, length, contentType, cacheControl, cancellationToken);
    }

    public async Task DeleteAsync(string key, CancellationToken cancellationToken)
    {
        try
        {
            await _client.DeleteObjectAsync(_config.Bucket, key, cancellationToken);
        }
        catch (AmazonS3Exception exception) when (IsNotFound(exception))
        {
        }
    }

    public static async Task<string> ComputeSha256Async(Stream stream, CancellationToken cancellationToken)
    {
        var hash = await SHA256.HashDataAsync(stream, cancellationToken);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    public void Dispose() => _client.Dispose();

    private async Task PutSmallObjectAsync(string key, byte[] content, string? contentType,
        string? cacheControl, CancellationToken cancellationToken)
    {
        using var stream = new MemoryStream(content, writable: false);
        var request = new PutObjectRequest
        {
            BucketName = _config.Bucket,
            Key = key,
            InputStream = stream,
            AutoCloseStream = false,
            AutoResetStreamPosition = true,
            UseChunkEncoding = false,
            ContentType = string.IsNullOrWhiteSpace(contentType) ? null : contentType
        };

        request.Headers.ContentLength = content.LongLength;
        if (!string.IsNullOrWhiteSpace(cacheControl))
        {
            request.Headers.CacheControl = cacheControl;
        }

        ApplyCompatibilityFlags(request);
        await _client.PutObjectAsync(request, cancellationToken);
    }

    private async Task MultipartUploadAsync(string key, Stream source, long length, string? contentType,
        string? cacheControl, CancellationToken cancellationToken)
    {
        var partCount = (length + _partSize - 1) / _partSize;
        if (partCount > MaximumPartCount)
        {
            throw new InvalidOperationException(
                $"对象 {key} 需要 {partCount} 个分片，超过 S3/COS 的 {MaximumPartCount} 上限；请增大 MIGRATION__PART_SIZE_MB。");
        }

        var initiateRequest = new InitiateMultipartUploadRequest
        {
            BucketName = _config.Bucket,
            Key = key,
            ContentType = string.IsNullOrWhiteSpace(contentType) ? null : contentType
        };

        if (!string.IsNullOrWhiteSpace(cacheControl))
        {
            initiateRequest.Headers.CacheControl = cacheControl;
        }

        var initiateResponse = await _client.InitiateMultipartUploadAsync(initiateRequest, cancellationToken);
        var uploadId = initiateResponse.UploadId;
        var partETags = new List<PartETag>((int)partCount);

        try
        {
            var remaining = length;
            var partNumber = 1;
            var buffer = GC.AllocateUninitializedArray<byte>((int)_partSize);

            while (remaining > 0)
            {
                var requested = (int)Math.Min(_partSize, remaining);
                var read = await ReadAtMostAsync(source, buffer.AsMemory(0, requested), cancellationToken);
                if (read == 0)
                {
                    throw new IOException($"源对象在传输中提前结束：还剩 {remaining} 字节未读取。");
                }

                using var partStream = new MemoryStream(buffer, 0, read, writable: false);
                var partRequest = new UploadPartRequest
                {
                    BucketName = _config.Bucket,
                    Key = key,
                    UploadId = uploadId,
                    PartNumber = partNumber,
                    PartSize = read,
                    IsLastPart = read == remaining,
                    InputStream = partStream,
                    UseChunkEncoding = false
                };

                ApplyCompatibilityFlags(partRequest);
                var partResponse = await _client.UploadPartAsync(partRequest, cancellationToken);
                partETags.Add(new PartETag(partNumber, partResponse.ETag));

                remaining -= read;
                partNumber++;
            }

            if (partETags.Count != partCount)
            {
                throw new IOException($"分片数量不一致：期望 {partCount}，实际 {partETags.Count}。");
            }

            await _client.CompleteMultipartUploadAsync(new CompleteMultipartUploadRequest
            {
                BucketName = _config.Bucket,
                Key = key,
                UploadId = uploadId,
                PartETags = partETags
            }, cancellationToken);
        }
        catch
        {
            try
            {
                await _client.AbortMultipartUploadAsync(new AbortMultipartUploadRequest
                {
                    BucketName = _config.Bucket,
                    Key = key,
                    UploadId = uploadId
                }, CancellationToken.None);
            }
            catch
            {
                // 忽略 abort 失败，原始异常更重要。
            }

            throw;
        }
    }

    private void ApplyCompatibilityFlags(PutObjectRequest request)
    {
        if (_config.DisableChecksum)
        {
            request.DisableDefaultChecksumValidation = true;
        }

        if (_config.DisablePayloadSigning)
        {
            request.DisablePayloadSigning = true;
        }
    }

    private void ApplyCompatibilityFlags(UploadPartRequest request)
    {
        if (_config.DisableChecksum)
        {
            request.DisableDefaultChecksumValidation = true;
        }

        if (_config.DisablePayloadSigning)
        {
            request.DisablePayloadSigning = true;
        }
    }

    private static ObjectMetadata ToMetadata(GetObjectMetadataResponse response)
    {
        return new ObjectMetadata(
            response.Headers.ContentLength,
            response.Headers.ContentType,
            response.CacheControl,
            response.ETag);
    }

    private static ObjectMetadata ToMetadata(GetObjectResponse response)
    {
        return new ObjectMetadata(
            response.Headers.ContentLength,
            response.Headers.ContentType,
            response.Headers.CacheControl,
            response.ETag);
    }

    private static async Task<int> ReadAtMostAsync(Stream stream, byte[] buffer, CancellationToken cancellationToken)
    {
        return await ReadAtMostAsync(stream, buffer.AsMemory(), cancellationToken);
    }

    private static async Task<int> ReadAtMostAsync(Stream stream, Memory<byte> buffer,
        CancellationToken cancellationToken)
    {
        var total = 0;
        while (total < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer[total..], cancellationToken);
            if (read == 0)
            {
                break;
            }

            total += read;
        }

        return total;
    }

    private static AmazonS3Client CreateClient(StorageEndpointConfig config)
    {
        var s3Config = new AmazonS3Config
        {
            ServiceURL = config.Endpoint,
            ForcePathStyle = config.ForcePathStyle,
            AuthenticationRegion = config.Region,
            Timeout = TimeSpan.FromMinutes(Math.Max(1, config.TimeoutMinutes)),
            MaxErrorRetry = Math.Max(0, config.MaxRetries),
            DisableLogging = true
        };

        return new AmazonS3Client(new BasicAWSCredentials(config.AccessKey, config.SecretKey), s3Config);
    }

    private static bool IsNotFound(AmazonS3Exception exception)
    {
        return exception.StatusCode == HttpStatusCode.NotFound ||
               string.Equals(exception.ErrorCode, "NoSuchKey", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(exception.ErrorCode, "NotFound", StringComparison.OrdinalIgnoreCase);
    }
}
