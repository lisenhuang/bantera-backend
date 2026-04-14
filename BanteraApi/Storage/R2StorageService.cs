using Amazon.Runtime;
using Amazon.S3;
using Amazon.S3.Model;
using Microsoft.Extensions.Options;

namespace BanteraApi.Storage;

public class R2StorageService
{
    private readonly AmazonS3Client _client;
    private readonly string _bucket;
    private readonly ILogger<R2StorageService> _logger;

    public R2StorageService(IOptions<R2Settings> options, ILogger<R2StorageService> logger)
    {
        _logger = logger;
        var settings = options.Value;
        _bucket = settings.BucketName;

        var credentials = new BasicAWSCredentials(settings.AccessKeyId, settings.SecretAccessKey);
        var config = new AmazonS3Config
        {
            ServiceURL = settings.ServiceUrl,
            ForcePathStyle = true,
        };

        _client = new AmazonS3Client(credentials, config);
    }

    public async Task<List<string>> ListObjectsAsync(CancellationToken ct = default)
    {
        var response = await _client.ListObjectsV2Async(new ListObjectsV2Request
        {
            BucketName = _bucket
        }, ct);

        return response.S3Objects.Select(o => o.Key).ToList();
    }

    public async Task UploadTextAsync(string key, string content, CancellationToken ct = default)
    {
        await _client.PutObjectAsync(new PutObjectRequest
        {
            BucketName = _bucket,
            Key = key,
            ContentBody = content,
            ContentType = "text/plain",
            UseChunkEncoding = false
        }, ct);

        _logger.LogInformation("Uploaded object: {Key}", key);
    }

    public async Task UploadObjectAsync(
        string key,
        Stream content,
        string contentType,
        CancellationToken ct = default)
    {
        await _client.PutObjectAsync(new PutObjectRequest
        {
            BucketName = _bucket,
            Key = key,
            InputStream = content,
            ContentType = contentType,
            AutoCloseStream = false,
            UseChunkEncoding = false,
        }, ct);

        _logger.LogInformation("Uploaded object: {Key}", key);
    }

    public async Task<string> DownloadTextAsync(string key, CancellationToken ct = default)
    {
        var response = await _client.GetObjectAsync(new GetObjectRequest
        {
            BucketName = _bucket,
            Key = key
        }, ct);

        using var reader = new StreamReader(response.ResponseStream);
        return await reader.ReadToEndAsync(ct);
    }

    public async Task DeleteObjectAsync(string key, CancellationToken ct = default)
    {
        await _client.DeleteObjectAsync(new DeleteObjectRequest
        {
            BucketName = _bucket,
            Key = key
        }, ct);

        _logger.LogInformation("Deleted object: {Key}", key);
    }

    public async Task<StoredObjectResult> DownloadObjectAsync(string key, CancellationToken ct = default)
    {
        var response = await _client.GetObjectAsync(new GetObjectRequest
        {
            BucketName = _bucket,
            Key = key
        }, ct);

        return new StoredObjectResult(
            response.ResponseStream,
            response.Headers.ContentType ?? "application/octet-stream",
            response.Headers.ContentLength);
    }
}

public sealed record StoredObjectResult(
    Stream Stream,
    string ContentType,
    long ContentLength);
