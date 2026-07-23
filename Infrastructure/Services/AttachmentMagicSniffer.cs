namespace Infrastructure.Services;

/// <summary>基于文件头魔数的轻量 Content-Type 嗅探（不信任客户端 MIME）。</summary>
public static class AttachmentMagicSniffer
{
    public static string? Sniff(ReadOnlySpan<byte> header)
    {
        if (header.Length >= 3
            && header[0] == 0xFF && header[1] == 0xD8 && header[2] == 0xFF)
            return "image/jpeg";

        if (header.Length >= 8
            && header[0] == 0x89 && header[1] == 0x50 && header[2] == 0x4E && header[3] == 0x47
            && header[4] == 0x0D && header[5] == 0x0A && header[6] == 0x1A && header[7] == 0x0A)
            return "image/png";

        if (header.Length >= 6
            && header[0] == (byte)'G' && header[1] == (byte)'I' && header[2] == (byte)'F'
            && header[3] == (byte)'8' && (header[4] == (byte)'7' || header[4] == (byte)'9')
            && header[5] == (byte)'a')
            return "image/gif";

        // RIFF....WEBP
        if (header.Length >= 12
            && header[0] == (byte)'R' && header[1] == (byte)'I' && header[2] == (byte)'F' && header[3] == (byte)'F'
            && header[8] == (byte)'W' && header[9] == (byte)'E' && header[10] == (byte)'B' && header[11] == (byte)'P')
            return "image/webp";

        if (header.Length >= 5
            && header[0] == (byte)'%' && header[1] == (byte)'P' && header[2] == (byte)'D'
            && header[3] == (byte)'F' && header[4] == (byte)'-')
            return "application/pdf";

        // ID3 or MPEG frame sync
        if (header.Length >= 3
            && header[0] == (byte)'I' && header[1] == (byte)'D' && header[2] == (byte)'3')
            return "audio/mpeg";
        if (header.Length >= 2 && header[0] == 0xFF && (header[1] & 0xE0) == 0xE0)
            return "audio/mpeg";

        // OggS
        if (header.Length >= 4
            && header[0] == (byte)'O' && header[1] == (byte)'g' && header[2] == (byte)'g' && header[3] == (byte)'S')
            return "audio/ogg";

        // ftyp box → video/mp4 (ISO BMFF)
        if (header.Length >= 12
            && header[4] == (byte)'f' && header[5] == (byte)'t' && header[6] == (byte)'y' && header[7] == (byte)'p')
            return "video/mp4";

        return null;
    }

    public static async Task<(string? ContentType, byte[] Header)> SniffAsync(
        Stream content,
        CancellationToken cancellationToken = default)
    {
        var header = new byte[16];
        var read = 0;
        while (read < header.Length)
        {
            var n = await content.ReadAsync(header.AsMemory(read, header.Length - read), cancellationToken)
                .ConfigureAwait(false);
            if (n == 0)
                break;
            read += n;
        }

        return (Sniff(header.AsSpan(0, read)), header[..read]);
    }
}
