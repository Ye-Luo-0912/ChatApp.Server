using System.Security.Cryptography;
using Core.Interfaces;
using Core.Interfaces.Cache;
using Core.Settings;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Jpeg;
using SixLabors.ImageSharp.Processing;

namespace Infrastructure.Services;

/// <summary>
/// 开发用本地头像存储；票据存 Redis，可跨实例。
/// </summary>
public sealed class LocalAvatarStorage(
    IOptions<AvatarStorageOptions> options,
    ICacheProvider cache,
    AvatarReencodeQueue reencodeQueue,
    ILogger<LocalAvatarStorage> logger) : IAvatarStorage
{
    private readonly AvatarStorageOptions _options = options.Value;
    private const int MaxPixels = 2048;
    private const int OutputSize = 512;

    public sealed record AvatarTicketInfo(long UserId, string ObjectKey, string ContentType, long ContentLength);

    public long MaxBytes => _options.MaxBytes;

    public bool IsAllowedContentType(string contentType) =>
        _options.AllowedContentTypes.Any(t =>
            string.Equals(t, contentType, StringComparison.OrdinalIgnoreCase));

    public async Task<(string ObjectKey, string Ticket, string UploadUrl, string PublicUrl, DateTimeOffset ExpiresAt)>
        CreateUploadTicketAsync(long userId, string contentType, long contentLength, CancellationToken cancellationToken = default)
    {
        if (!IsAllowedContentType(contentType))
            throw new ArgumentException("不支持的头像格式");
        if (contentLength <= 0 || contentLength > MaxBytes)
            throw new ArgumentException($"头像大小须在 1~{MaxBytes} 字节之间");

        var objectKey = $"{userId}/{DateTime.UtcNow:yyyyMMddHHmmss}-{Guid.NewGuid():N}.jpg";
        var ticket = Convert.ToHexString(RandomNumberGenerator.GetBytes(24));
        var expires = DateTimeOffset.UtcNow.AddMinutes(Math.Clamp(_options.TicketMinutes, 1, 60));
        var publicUrl = $"{_options.PublicBaseUrl.TrimEnd('/')}/{objectKey}";
        var ttl = expires - DateTimeOffset.UtcNow;

        await cache.SetStringPayloadAsync(
            TicketKey(ticket),
            new AvatarTicketInfo(userId, objectKey, contentType, contentLength),
            ttl,
            cancellationToken).ConfigureAwait(false);

        var uploadUrl = $"/api/users/me/avatar/upload?ticket={Uri.EscapeDataString(ticket)}";
        return (objectKey, ticket, uploadUrl, publicUrl, expires);
    }

    public async Task<(bool Ok, string? PublicUrl, string? Error)> StoreAsync(
        long userId, string ticket, Stream content, string contentType, CancellationToken cancellationToken = default)
    {
        var info = await cache.GetStringPayloadAsync<AvatarTicketInfo>(TicketKey(ticket), cancellationToken)
            .ConfigureAwait(false);
        if (info is null)
            return (false, null, "上传票无效或已过期");
        if (info.UserId != userId)
            return (false, null, "上传票与用户不匹配");
        if (!IsAllowedContentType(contentType))
            return (false, null, "不支持的头像格式");

        await using var buffer = new MemoryStream();
        await content.CopyToAsync(buffer, cancellationToken).ConfigureAwait(false);
        if (buffer.Length <= 0 || buffer.Length > MaxBytes)
            return (false, null, "头像大小超限");

        buffer.Position = 0;
        byte[] encoded;
        try
        {
            encoded = await reencodeQueue.RunAsync(async ct =>
            {
                buffer.Position = 0;
                return await ReencodeAsync(buffer, ct).ConfigureAwait(false);
            }, cancellationToken).ConfigureAwait(false);
        }
        catch (UnknownImageFormatException)
        {
            return (false, null, "无法解码为有效图片");
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "头像解码失败");
            return (false, null, "图片处理失败");
        }

        var root = Path.GetFullPath(_options.LocalRootPath);
        var fullPath = Path.GetFullPath(Path.Combine(root, info.ObjectKey.Replace('/', Path.DirectorySeparatorChar)));
        if (!fullPath.StartsWith(root, StringComparison.OrdinalIgnoreCase))
            return (false, null, "非法对象键");

        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        await File.WriteAllBytesAsync(fullPath, encoded, cancellationToken).ConfigureAwait(false);
        await cache.RemoveAsync(TicketKey(ticket), cancellationToken).ConfigureAwait(false);

        var publicUrl = $"{_options.PublicBaseUrl.TrimEnd('/')}/{info.ObjectKey}";
        return (true, publicUrl, null);
    }

    public async Task<(bool Ok, string? PublicUrl, string? Error)> ConfirmObjectAsync(
        long userId, string objectKey, CancellationToken cancellationToken = default)
    {
        if (!objectKey.StartsWith($"{userId}/", StringComparison.Ordinal))
            return (false, null, "无效的头像对象键");
        if (!await ObjectExistsAsync(objectKey, cancellationToken).ConfigureAwait(false))
            return (false, null, "头像尚未上传完成");
        return (true, GetPublicUrl(objectKey), null);
    }

    public Task<bool> ObjectExistsAsync(string objectKey, CancellationToken cancellationToken = default)
    {
        var root = Path.GetFullPath(_options.LocalRootPath);
        var fullPath = Path.GetFullPath(Path.Combine(root, objectKey.Replace('/', Path.DirectorySeparatorChar)));
        if (!fullPath.StartsWith(root, StringComparison.OrdinalIgnoreCase))
            return Task.FromResult(false);
        return Task.FromResult(File.Exists(fullPath));
    }

    public string? GetPublicUrl(string objectKey) =>
        string.IsNullOrWhiteSpace(objectKey) ? null : $"{_options.PublicBaseUrl.TrimEnd('/')}/{objectKey}";

    public Task TryDeleteAsync(string? objectKeyOrUrl, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(objectKeyOrUrl))
            return Task.CompletedTask;

        try
        {
            var key = objectKeyOrUrl;
            var prefix = _options.PublicBaseUrl.TrimEnd('/') + "/";
            if (key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                key = key[prefix.Length..];

            var root = Path.GetFullPath(_options.LocalRootPath);
            var fullPath = Path.GetFullPath(Path.Combine(root, key.Replace('/', Path.DirectorySeparatorChar)));
            if (fullPath.StartsWith(root, StringComparison.OrdinalIgnoreCase) && File.Exists(fullPath))
                File.Delete(fullPath);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "删除旧头像失败");
        }

        return Task.CompletedTask;
    }

    private static async Task<byte[]> ReencodeAsync(Stream input, CancellationToken cancellationToken)
    {
        using var image = await Image.LoadAsync(input, cancellationToken).ConfigureAwait(false);
        if (image.Width > MaxPixels || image.Height > MaxPixels)
            throw new InvalidOperationException("图片像素尺寸超限");

        image.Mutate(x => x.Resize(new ResizeOptions
        {
            Size = new Size(OutputSize, OutputSize),
            Mode = ResizeMode.Crop,
        }));

        await using var output = new MemoryStream();
        await image.SaveAsJpegAsync(output, new JpegEncoder { Quality = 85 }, cancellationToken)
            .ConfigureAwait(false);
        return output.ToArray();
    }

    private static string TicketKey(string ticket) => $"avatar:ticket:{ticket}";
}
