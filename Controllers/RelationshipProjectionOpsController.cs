using ChatApp.Realtime.Abstractions.Relationships;
using ChatApp.Server.Services;
using Core.Settings;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using System.Security.Cryptography;
using System.Text;

namespace ChatApp.Server.Controllers;

[ApiController]
[Route("api/ops/relationship-projection")]
public sealed class RelationshipProjectionOpsController(
    IRelationshipProjectionSnapshotExportService snapshots,
    IOptions<RelationshipProjectionExportOptions> options) : ControllerBase
{
    [HttpGet("streams")]
    public async Task<ActionResult<RelationshipProjectionStreamPage>> ListStreams(
        [FromQuery] long? afterOwnerUserId,
        [FromQuery] RelationshipProjectionListType? afterListType,
        [FromQuery] int limit = 100,
        CancellationToken cancellationToken = default)
    {
        var authorizationFailure = AuthorizeExport();
        if (authorizationFailure is not null)
            return authorizationFailure;
        return await snapshots.ListStreamsAsync(
            afterOwnerUserId,
            afterListType,
            limit,
            cancellationToken).ConfigureAwait(false);
    }

    [HttpGet("streams/{ownerUserId:long}/{listType}")]
    public async Task<ActionResult<RelationshipProjectionStreamSnapshot>> GetStream(
        [FromRoute] long ownerUserId,
        [FromRoute] RelationshipProjectionListType listType,
        CancellationToken cancellationToken = default)
    {
        var authorizationFailure = AuthorizeExport();
        if (authorizationFailure is not null)
            return authorizationFailure;
        return await snapshots.ReadStreamAsync(ownerUserId, listType, cancellationToken)
            .ConfigureAwait(false);
    }

    [HttpGet("streams/{ownerUserId:long}/{listType}/digest")]
    public async Task<ActionResult<RelationshipProjectionStreamDigest>> GetStreamDigest(
        [FromRoute] long ownerUserId,
        [FromRoute] RelationshipProjectionListType listType,
        CancellationToken cancellationToken = default)
    {
        var authorizationFailure = AuthorizeExport();
        if (authorizationFailure is not null)
            return authorizationFailure;
        return await snapshots.ReadStreamDigestAsync(ownerUserId, listType, cancellationToken)
            .ConfigureAwait(false);
    }

    private ActionResult? AuthorizeExport()
    {
        var configured = options.Value.ApiKey?.Trim();
        if (string.IsNullOrEmpty(configured))
            return StatusCode(StatusCodes.Status503ServiceUnavailable);
        if (!Request.Headers.TryGetValue(
                RelationshipProjectionExportOptions.ApiKeyHeaderName,
                out var provided))
        {
            return Unauthorized();
        }

        var expectedHash = SHA256.HashData(Encoding.UTF8.GetBytes(configured));
        var providedHash = SHA256.HashData(Encoding.UTF8.GetBytes(provided.ToString()));
        return CryptographicOperations.FixedTimeEquals(expectedHash, providedHash)
            ? null
            : Unauthorized();
    }
}
