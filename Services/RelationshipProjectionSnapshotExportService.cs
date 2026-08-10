using ChatApp.Realtime.Abstractions.Relationships;

namespace ChatApp.Server.Services;

public interface IRelationshipProjectionSnapshotExportService
{
    Task<RelationshipProjectionStreamPage> ListStreamsAsync(
        long? afterOwnerUserId,
        RelationshipProjectionListType? afterListType,
        int limit,
        CancellationToken ct = default);

    Task<RelationshipProjectionStreamSnapshot> ReadStreamAsync(
        long ownerUserId,
        RelationshipProjectionListType listType,
        CancellationToken ct = default);

    Task<RelationshipProjectionStreamDigest> ReadStreamDigestAsync(
        long ownerUserId,
        RelationshipProjectionListType listType,
        CancellationToken ct = default);
}

internal sealed class RelationshipProjectionSnapshotExportService(
    Infrastructure.Services.RelationshipProjectionSnapshotReader reader)
    : IRelationshipProjectionSnapshotExportService
{
    public Task<RelationshipProjectionStreamPage> ListStreamsAsync(
        long? afterOwnerUserId,
        RelationshipProjectionListType? afterListType,
        int limit,
        CancellationToken ct = default) =>
        reader.ListStreamsAsync(afterOwnerUserId, afterListType, limit, ct);

    public Task<RelationshipProjectionStreamSnapshot> ReadStreamAsync(
        long ownerUserId,
        RelationshipProjectionListType listType,
        CancellationToken ct = default) =>
        reader.ReadStreamAsync(ownerUserId, listType, ct);

    public Task<RelationshipProjectionStreamDigest> ReadStreamDigestAsync(
        long ownerUserId,
        RelationshipProjectionListType listType,
        CancellationToken ct = default) =>
        reader.ReadStreamDigestAsync(ownerUserId, listType, ct);
}
