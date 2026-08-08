namespace Core.Models.Friend;

/// <summary>
/// 两个用户之间的完整关系快照。A/B 是一次读取中固定的方向，所有关系迁移
/// 都必须基于同一个快照判断，不能只读取其中一条 friendship 行。
/// </summary>
public sealed class PairRelationshipState
{
    public long UserAId { get; init; }
    public long UserBId { get; init; }
    public bool AHasB { get; init; }
    public bool BHasA { get; init; }
    public bool ABlocksB { get; init; }
    public bool BBlocksA { get; init; }
    public PairRequestDirection PendingDirection { get; init; }

    public bool IsBlocked => ABlocksB || BBlocksA;
    public bool IsMutualFriendship => AHasB && BHasA;
}

public enum PairRequestDirection : byte
{
    None = 0,
    AToB = 1,
    BToA = 2,
}
