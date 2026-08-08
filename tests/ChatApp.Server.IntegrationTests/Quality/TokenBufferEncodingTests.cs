using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Core.Caching;
using Core.Models.Token;
using Xunit;

namespace ChatApp.Server.IntegrationTests.Quality;

public sealed class TokenBufferEncodingTests
{
    [Fact]
    public void Base64UrlEncoding_MatchesStandardWithoutPadding()
    {
        Assert.Equal("-_8", TokenBufferEncoding.EncodeBase64Url([0xFB, 0xFF]));
        Assert.Equal(
            Convert.ToBase64String(SHA256.HashData(Encoding.UTF8.GetBytes("token")))
                .TrimEnd('=')
                .Replace('+', '-')
                .Replace('/', '_'),
            TokenHasher.Hash("token"));
    }

    [Fact]
    public void RandomEncoders_UseExpectedLengthsAndAlphabet()
    {
        var base64 = TokenBufferEncoding.CreateBase64Url(16);
        Assert.Equal(OpaqueTokenFormat.GetBase64UrlLength(16), base64.Length);
        Assert.True(OpaqueTokenFormat.IsBase64UrlToken(base64, 16));

        var hex = TokenBufferEncoding.CreateHex(24);
        Assert.Equal(48, hex.Length);
        Assert.All(hex, character => Assert.True(char.IsAsciiHexDigit(character)));

        var grouped = TokenBufferEncoding.CreateGroupedHex(16, groupBytes: 4);
        Assert.Equal(35, grouped.Length);
        Assert.Equal(3, grouped.Count(character => character == '-'));
        Assert.All(grouped.Where(character => character != '-'),
            character => Assert.True(char.IsAsciiHexDigit(character)));
    }

    [Fact]
    public void RandomEncoders_SlicePooledBuffersToRequestedLength()
    {
        const int byteLength = 257;

        var base64 = TokenBufferEncoding.CreateBase64Url(byteLength);
        Assert.Equal(OpaqueTokenFormat.GetBase64UrlLength(byteLength), base64.Length);

        var hex = TokenBufferEncoding.CreateHex(byteLength);
        Assert.Equal(byteLength * 2, hex.Length);

        var grouped = TokenBufferEncoding.CreateGroupedHex(byteLength, groupBytes: 16);
        Assert.Equal(byteLength * 2 + (byteLength - 1) / 16, grouped.Length);
    }

    [Fact]
    public void LargeDeviceId_UsesTheSameHashAsUtf8Input()
    {
        var deviceId = new string('d', 513);
        var expected = BinaryPrimitives.ReadUInt64BigEndian(
            SHA256.HashData(Encoding.UTF8.GetBytes(deviceId)));

        Assert.Equal(expected, DeviceIdHashHelper.Compute(deviceId));
    }

    [Fact]
    public void AccessTokenClaimFormatting_IsCachedAndExcludedFromPayload()
    {
        var data = new AccessTokenData
        {
            UserId = 9223372036854770000,
            UserName = "allocation-test",
            ExpiresAtMs = DateTimeOffset.UtcNow.AddMinutes(5).ToUnixTimeMilliseconds(),
            DeviceIdHash = 0x0123456789ABCDEF,
            SecurityVersion = 1,
        };

        var userIdText = data.UserIdText;
        var deviceHashText = data.DeviceIdHashText;

        Assert.Same(userIdText, data.UserIdText);
        Assert.Same(deviceHashText, data.DeviceIdHashText);
        Assert.Equal("9223372036854770000", userIdText);
        Assert.Equal("0123456789abcdef", deviceHashText);

        var json = JsonSerializer.Serialize(data);
        Assert.DoesNotContain("UserIdText", json, StringComparison.Ordinal);
        Assert.DoesNotContain("DeviceIdHashText", json, StringComparison.Ordinal);
    }
}
