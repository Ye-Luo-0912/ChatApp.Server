using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using Core.Models.Token;
using Xunit;

namespace ChatApp.Server.IntegrationTests.Auth;

public sealed class DeviceIdHashTests
{
    [Fact]
    public void Compute_HashesAcceptedRawDeviceIdsConsistently()
    {
        string[] deviceIds =
        [
            "0123456789abcdef",
            "browser-device_001.abc",
            new('z', 128),
        ];

        foreach (var deviceId in deviceIds)
        {
            var digest = SHA256.HashData(Encoding.UTF8.GetBytes(deviceId));
            var expected = BinaryPrimitives.ReadUInt64BigEndian(digest);

            Assert.Equal(expected, DeviceIdHashHelper.Compute(deviceId));
            Assert.True(DeviceIdHashHelper.Verify(deviceId, expected));
        }
    }

    [Fact]
    public void Compute_RejectsMissingId_AndDistinguishesRawIds()
    {
        Assert.Null(DeviceIdHashHelper.Compute(null));
        Assert.Null(DeviceIdHashHelper.Compute(string.Empty));
        Assert.NotEqual(
            DeviceIdHashHelper.Compute("0123456789abcdef"),
            DeviceIdHashHelper.Compute("0123456789abcdeg"));
    }
}
