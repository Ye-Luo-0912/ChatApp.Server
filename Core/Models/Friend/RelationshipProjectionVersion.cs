namespace Core.Models.Friend;

/// <summary>
/// Server-owned contiguous version clock for one user's relationship list projection.
/// </summary>
public sealed class RelationshipProjectionVersion
{
    public long OwnerUserId { get; set; }
    public byte ListType { get; set; }
    public long Version { get; set; }
    public long UpdatedAtMs { get; set; }
}
