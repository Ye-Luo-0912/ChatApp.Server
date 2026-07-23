using System.Text.Json;
using System.Text.RegularExpressions;
using Core.Models.Export;

namespace Infrastructure.Services;

/// <summary>
/// 从消息正文提取附件清单（legacy fallback）。
/// 正式附件优先走 realtime.attachments；本解析器覆盖无元数据行的 JSON 信封 / URL 扫描。
/// </summary>
public static partial class ChatExportAttachmentParser
{
    private static readonly Regex HttpUrl = CreateHttpUrlRegex();

    public static IReadOnlyList<ChatExportAttachmentItem> Extract(
        string messageId,
        long receivedAtMs,
        string content,
        int urlScanMaxContentChars = 64 * 1024,
        bool skipUrlScan = false)
    {
        if (string.IsNullOrWhiteSpace(content))
            return [];

        var items = new List<ChatExportAttachmentItem>();
        var trimmed = content.Trim();
        if (trimmed.StartsWith('{') || trimmed.StartsWith('['))
        {
            try
            {
                using var doc = JsonDocument.Parse(trimmed);
                CollectFromJson(doc.RootElement, messageId, receivedAtMs, items);
            }
            catch (JsonException)
            {
                // fall through to URL scan
            }
        }

        if (skipUrlScan || content.Length > Math.Max(0, urlScanMaxContentChars))
            return items;

        foreach (Match match in HttpUrl.Matches(content))
        {
            var url = match.Value.TrimEnd(')', ']', '.', ',', ';', '"', '\'');
            if (url.Length < 8)
                continue;
            if (items.Exists(i => string.Equals(i.Url, url, StringComparison.Ordinal)))
                continue;
            items.Add(new ChatExportAttachmentItem(
                MessageId: messageId,
                ReceivedAtMs: receivedAtMs,
                Url: url,
                Name: null,
                ContentType: GuessContentType(url),
                SizeBytes: null,
                Source: "url_scan"));
        }

        return items;
    }

    private static void CollectFromJson(
        JsonElement el,
        string messageId,
        long receivedAtMs,
        List<ChatExportAttachmentItem> items)
    {
        switch (el.ValueKind)
        {
            case JsonValueKind.Object:
                if (TryReadAttachmentObject(el, messageId, receivedAtMs, out var one))
                    items.Add(one);

                if (el.TryGetProperty("attachments", out var attachments))
                    CollectFromJson(attachments, messageId, receivedAtMs, items);
                if (el.TryGetProperty("attachment", out var attachment))
                    CollectFromJson(attachment, messageId, receivedAtMs, items);
                if (el.TryGetProperty("files", out var files))
                    CollectFromJson(files, messageId, receivedAtMs, items);
                break;

            case JsonValueKind.Array:
                foreach (var child in el.EnumerateArray())
                    CollectFromJson(child, messageId, receivedAtMs, items);
                break;
        }
    }

    private static bool TryReadAttachmentObject(
        JsonElement el,
        string messageId,
        long receivedAtMs,
        out ChatExportAttachmentItem item)
    {
        item = null!;
        string? url = null;
        if (el.TryGetProperty("url", out var urlEl) && urlEl.ValueKind == JsonValueKind.String)
            url = urlEl.GetString();
        else if (el.TryGetProperty("uri", out var uriEl) && uriEl.ValueKind == JsonValueKind.String)
            url = uriEl.GetString();
        else if (el.TryGetProperty("href", out var hrefEl) && hrefEl.ValueKind == JsonValueKind.String)
            url = hrefEl.GetString();

        if (string.IsNullOrWhiteSpace(url))
            return false;

        string? name = null;
        if (el.TryGetProperty("name", out var nameEl) && nameEl.ValueKind == JsonValueKind.String)
            name = nameEl.GetString();
        else if (el.TryGetProperty("fileName", out var fileNameEl) && fileNameEl.ValueKind == JsonValueKind.String)
            name = fileNameEl.GetString();

        string? contentType = null;
        if (el.TryGetProperty("contentType", out var ctEl) && ctEl.ValueKind == JsonValueKind.String)
            contentType = ctEl.GetString();
        else if (el.TryGetProperty("mime", out var mimeEl) && mimeEl.ValueKind == JsonValueKind.String)
            contentType = mimeEl.GetString();
        else if (el.TryGetProperty("mimeType", out var mimeTypeEl) && mimeTypeEl.ValueKind == JsonValueKind.String)
            contentType = mimeTypeEl.GetString();

        long? size = null;
        if (el.TryGetProperty("size", out var sizeEl) && sizeEl.TryGetInt64(out var sizeVal))
            size = sizeVal;
        else if (el.TryGetProperty("sizeBytes", out var sizeBytesEl) && sizeBytesEl.TryGetInt64(out var sizeBytes))
            size = sizeBytes;

        item = new ChatExportAttachmentItem(
            MessageId: messageId,
            ReceivedAtMs: receivedAtMs,
            Url: url!,
            Name: name,
            ContentType: contentType ?? GuessContentType(url!),
            SizeBytes: size,
            Source: "json");
        return true;
    }

    private static string? GuessContentType(string url)
    {
        var path = url.Split('?', 2)[0];
        var ext = Path.GetExtension(path);
        return ext.ToLowerInvariant() switch
        {
            ".png" => "image/png",
            ".jpg" or ".jpeg" => "image/jpeg",
            ".gif" => "image/gif",
            ".webp" => "image/webp",
            ".mp4" => "video/mp4",
            ".pdf" => "application/pdf",
            ".zip" => "application/zip",
            _ => null,
        };
    }

    [GeneratedRegex(@"https?://[^\s<>""']+", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex CreateHttpUrlRegex();
}
