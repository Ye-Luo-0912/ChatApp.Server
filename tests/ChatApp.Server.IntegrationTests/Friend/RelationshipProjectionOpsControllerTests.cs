using ChatApp.Realtime.Abstractions.Relationships;
using ChatApp.Server.Controllers;
using ChatApp.Server.Services;
using Core.Settings;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using Xunit;

namespace ChatApp.Server.IntegrationTests.Friend;

public sealed class RelationshipProjectionOpsControllerTests
{
    [Fact]
    public async Task ExportEndpoints_FailClosedWhenServiceKeyIsNotConfigured()
    {
        var reader = new RecordingSnapshotReader();
        var controller = CreateController(reader, apiKey: null, providedKey: null);

        var response = await controller.ListStreams(null, null, cancellationToken: CancellationToken.None);

        var failure = Assert.IsType<StatusCodeResult>(response.Result);
        Assert.Equal(StatusCodes.Status503ServiceUnavailable, failure.StatusCode);
        Assert.Equal(0, reader.ListCalls);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("wrong-key")]
    public async Task ExportEndpoints_RejectMissingOrInvalidServiceKey(string? providedKey)
    {
        var reader = new RecordingSnapshotReader();
        var controller = CreateController(reader, "expected-key", providedKey);

        var response = await controller.GetStream(
            42,
            RelationshipProjectionListType.Friends,
            CancellationToken.None);

        Assert.IsType<UnauthorizedResult>(response.Result);
        Assert.Equal(0, reader.ReadCalls);
    }

    [Fact]
    public async Task ExportEndpoints_AcceptMatchingServiceKeyAndReturnSnapshotPage()
    {
        var reader = new RecordingSnapshotReader();
        var controller = CreateController(reader, "expected-key", "expected-key");

        var response = await controller.ListStreams(
            40,
            RelationshipProjectionListType.Friends,
            25,
            CancellationToken.None);

        var page = Assert.IsType<RelationshipProjectionStreamPage>(response.Value);
        var stream = Assert.Single(page.Items);
        Assert.Equal(42, stream.OwnerUserId);
        Assert.Equal(1, reader.ListCalls);
    }

    [Fact]
    public async Task DigestEndpoint_ReturnsOnlyStreamMetadata()
    {
        var reader = new RecordingSnapshotReader();
        var controller = CreateController(reader, "expected-key", "expected-key");

        var response = await controller.GetStreamDigest(
            42,
            RelationshipProjectionListType.Friends,
            CancellationToken.None);

        var digest = Assert.IsType<RelationshipProjectionStreamDigest>(response.Value);
        Assert.Equal(42, digest.OwnerUserId);
        Assert.Equal(3, digest.Version);
        Assert.Equal(0, digest.ItemCount);
        Assert.Equal(RelationshipProjectionSnapshotHash.Compute([]), digest.ResourceHash);
        Assert.Equal(1, reader.DigestCalls);
        Assert.Equal(0, reader.ReadCalls);
    }

    private static RelationshipProjectionOpsController CreateController(
        IRelationshipProjectionSnapshotExportService reader,
        string? apiKey,
        string? providedKey)
    {
        var controller = new RelationshipProjectionOpsController(
            reader,
            Options.Create(new RelationshipProjectionExportOptions { ApiKey = apiKey }));
        var httpContext = new DefaultHttpContext();
        if (providedKey is not null)
        {
            httpContext.Request.Headers[
                RelationshipProjectionExportOptions.ApiKeyHeaderName] = providedKey;
        }

        controller.ControllerContext = new ControllerContext { HttpContext = httpContext };
        return controller;
    }

    private sealed class RecordingSnapshotReader : IRelationshipProjectionSnapshotExportService
    {
        public int ListCalls { get; private set; }
        public int ReadCalls { get; private set; }
        public int DigestCalls { get; private set; }

        public Task<RelationshipProjectionStreamPage> ListStreamsAsync(
            long? afterOwnerUserId,
            RelationshipProjectionListType? afterListType,
            int limit,
            CancellationToken cancellationToken = default)
        {
            ListCalls++;
            return Task.FromResult(new RelationshipProjectionStreamPage(
                [new RelationshipProjectionStreamDescriptor(
                    42,
                    RelationshipProjectionListType.Friends,
                    3)],
                false,
                null,
                null));
        }

        public Task<RelationshipProjectionStreamSnapshot> ReadStreamAsync(
            long ownerUserId,
            RelationshipProjectionListType listType,
            CancellationToken cancellationToken = default)
        {
            ReadCalls++;
            var hash = RelationshipProjectionSnapshotHash.Compute([]);
            return Task.FromResult(new RelationshipProjectionStreamSnapshot
            {
                SnapshotId = "snapshot",
                OwnerUserId = ownerUserId,
                ListType = listType,
                Version = 0,
                CapturedAtMs = 1,
                ItemCount = 0,
                ResourceHash = hash,
                Items = []
            });
        }

        public Task<RelationshipProjectionStreamDigest> ReadStreamDigestAsync(
            long ownerUserId,
            RelationshipProjectionListType listType,
            CancellationToken cancellationToken = default)
        {
            DigestCalls++;
            return Task.FromResult(new RelationshipProjectionStreamDigest(
                ownerUserId,
                listType,
                3,
                0,
                RelationshipProjectionSnapshotHash.Compute([]),
                1));
        }
    }
}
