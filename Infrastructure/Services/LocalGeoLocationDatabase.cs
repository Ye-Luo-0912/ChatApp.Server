using System.Net;
using Core.Settings;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Infrastructure.Services;

/// <summary>
/// Dependency-free local GeoIP reader. The file is a longest-prefix CIDR
/// table with one record per line: <c>network/prefix|country|city</c>.
/// It is loaded once at startup and performs no I/O during lookup.
/// </summary>
public sealed class LocalGeoLocationDatabase
{
    private readonly Entry[] _entries;

    public LocalGeoLocationDatabase(
        IOptions<GeoLocationOptions> options,
        ILogger<LocalGeoLocationDatabase> logger)
    {
        var path = options.Value.LocalDatabasePath;
        if (string.IsNullOrWhiteSpace(path))
        {
            _entries = [];
            return;
        }

        var resolvedPath = Path.IsPathRooted(path)
            ? path
            : Path.Combine(AppContext.BaseDirectory, path);
        if (!File.Exists(resolvedPath))
        {
            logger.LogWarning("本地 GeoIP 数据库不存在 Path={Path}，将按无本地命中处理", resolvedPath);
            _entries = [];
            return;
        }

        var maxEntries = Math.Clamp(options.Value.MaxLocalEntries, 1, 1_000_000);
        var loaded = new List<Entry>(Math.Min(maxEntries, 16_384));
        foreach (var rawLine in File.ReadLines(resolvedPath))
        {
            if (loaded.Count >= maxEntries)
                break;

            var line = rawLine.Trim();
            if (line.Length == 0 || line[0] == '#')
                continue;

            var columns = line.Split('|', 3, StringSplitOptions.TrimEntries);
            if (columns.Length != 3
                || !TryParseCidr(columns[0], out var network, out var prefixLength)
                || string.IsNullOrWhiteSpace(columns[1])
                || string.IsNullOrWhiteSpace(columns[2]))
            {
                logger.LogWarning("忽略格式无效的本地 GeoIP 数据行 Path={Path}", resolvedPath);
                continue;
            }

            loaded.Add(new Entry(
                network.GetAddressBytes(),
                network.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6,
                prefixLength,
                $"{columns[1]}>{columns[2]}"));
        }

        _entries = loaded
            .OrderByDescending(x => x.PrefixLength)
            .ToArray();
        logger.LogInformation("已加载本地 GeoIP CIDR 数据库 Entries={Entries}", _entries.Length);
    }

    public bool TryGetLocation(string clientIp, out string? location)
    {
        location = null;
        if (!IPAddress.TryParse(clientIp, out var address))
            return false;

        if (address.IsIPv4MappedToIPv6)
            address = address.MapToIPv4();

        var bytes = address.GetAddressBytes();
        var ipv6 = address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6;
        foreach (var entry in _entries)
        {
            if (entry.IsIpv6 != ipv6 || entry.Network.Length != bytes.Length)
                continue;

            if (!Matches(entry.Network, bytes, entry.PrefixLength))
                continue;

            location = entry.Location;
            return true;
        }

        return false;
    }

    private static bool Matches(byte[] network, byte[] address, int prefixLength)
    {
        var fullBytes = prefixLength / 8;
        for (var i = 0; i < fullBytes; i++)
        {
            if (network[i] != address[i])
                return false;
        }

        var remainingBits = prefixLength % 8;
        if (remainingBits == 0)
            return true;

        var mask = (byte)(0xFF << (8 - remainingBits));
        return (network[fullBytes] & mask) == (address[fullBytes] & mask);
    }

    private static bool TryParseCidr(
        string value,
        out IPAddress network,
        out int prefixLength)
    {
        network = IPAddress.None;
        prefixLength = 0;
        var separator = value.IndexOf('/');
        if (separator <= 0
            || separator == value.Length - 1
            || !IPAddress.TryParse(value[..separator], out var parsed)
            || !int.TryParse(value[(separator + 1)..], out var parsedPrefix))
        {
            return false;
        }

        if (parsed.IsIPv4MappedToIPv6)
            parsed = parsed.MapToIPv4();

        var maxPrefix = parsed.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6
            ? 128
            : 32;
        if (parsedPrefix < 0 || parsedPrefix > maxPrefix)
            return false;

        var bytes = parsed.GetAddressBytes();
        var fullBytes = parsedPrefix / 8;
        var remainingBits = parsedPrefix % 8;
        if (remainingBits != 0)
        {
            var mask = (byte)(0xFF << (8 - remainingBits));
            bytes[fullBytes] &= mask;
            fullBytes++;
        }

        for (var i = fullBytes; i < bytes.Length; i++)
            bytes[i] = 0;

        network = new IPAddress(bytes);
        prefixLength = parsedPrefix;
        return true;
    }

    private sealed record Entry(
        byte[] Network,
        bool IsIpv6,
        int PrefixLength,
        string Location);
}
