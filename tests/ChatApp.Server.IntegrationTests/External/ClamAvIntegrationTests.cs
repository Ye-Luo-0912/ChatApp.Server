using System.Text;
using System.Net;
using System.Net.Sockets;
using Core.Settings;
using Infrastructure.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace ChatApp.Server.IntegrationTests.External;

public sealed class ClamAvIntegrationTests
{
    [Fact]
    public async Task ReadinessProbe_UsesClamProtocolPingAndRequiresPong()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var server = Task.Run(async () =>
        {
            using var client = await listener.AcceptTcpClientAsync();
            await using var stream = client.GetStream();
            var request = new byte[16];
            var length = 0;
            while (length < request.Length)
            {
                var read = await stream.ReadAsync(request.AsMemory(length));
                if (read == 0)
                    break;
                length += read;
                if (request.AsSpan(0, length).Contains((byte)0))
                    break;
            }

            Assert.Equal("zPING\0", Encoding.ASCII.GetString(request, 0, length));
            await stream.WriteAsync("PONG\n"u8.ToArray());
        });

        var scanner = new ClamAvAttachmentContentScanner(
            Options.Create(new AttachmentStorageOptions
            {
                ClamAvHost = IPAddress.Loopback.ToString(),
                ClamAvPort = port,
                ClamAvTimeoutMilliseconds = 2_000,
            }),
            NullLogger<ClamAvAttachmentContentScanner>.Instance);

        await scanner.ProbeAsync();
        await server;
    }

    [SkippableFact]
    [Trait("Category", "ClamAV")]
    public async Task Instream_AllowsBenignContent_AndRejectsEicar()
    {
        var endpoint = Environment.GetEnvironmentVariable("CHATAPP_TEST_CLAMAV");
        Skip.If(string.IsNullOrWhiteSpace(endpoint),
            "Set CHATAPP_TEST_CLAMAV=host:port to run the ClamAV integration gate.");

        var separator = endpoint!.LastIndexOf(':');
        var port = 0;
        Skip.If(separator <= 0 || !int.TryParse(endpoint[(separator + 1)..], out port),
            "CHATAPP_TEST_CLAMAV must use host:port.");

        var scanner = new ClamAvAttachmentContentScanner(
            Options.Create(new AttachmentStorageOptions
            {
                ClamAvHost = endpoint[..separator],
                ClamAvPort = port,
                ClamAvTimeoutMilliseconds = 30_000,
                ClamAvEngineVersion = "integration",
            }),
            NullLogger<ClamAvAttachmentContentScanner>.Instance);

        await using var benign = new MemoryStream(Encoding.UTF8.GetBytes("chatapp benign payload"));
        var allowed = await scanner.ScanAsync(benign, "application/octet-stream", "note.bin");
        Assert.True(allowed.Allowed, allowed.Reason);
        Assert.Equal("ClamAV", allowed.EngineName);

        const string eicar =
            "X5O!P%@AP[4\\PZX54(P^)7CC)7}$EICAR-STANDARD-ANTIVIRUS-TEST-FILE!$H+H*";
        await using var malware = new MemoryStream(Encoding.ASCII.GetBytes(eicar));
        var denied = await scanner.ScanAsync(malware, "application/octet-stream", "eicar.com");
        Assert.False(denied.Allowed);
        Assert.False(denied.IsTransient);
        Assert.Contains("FOUND", denied.Reason ?? string.Empty, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task UnreachableEndpoint_IsTransient()
    {
        var scanner = new ClamAvAttachmentContentScanner(
            Options.Create(new AttachmentStorageOptions
            {
                ClamAvHost = "127.0.0.1",
                ClamAvPort = 1,
                ClamAvTimeoutMilliseconds = 1_000,
            }),
            NullLogger<ClamAvAttachmentContentScanner>.Instance);

        await using var content = new MemoryStream([1, 2, 3]);
        var result = await scanner.ScanAsync(content, "application/octet-stream", "probe.bin");

        Assert.False(result.Allowed);
        Assert.True(result.IsTransient);
        Assert.Equal("ClamAV", result.EngineName);
    }
}
