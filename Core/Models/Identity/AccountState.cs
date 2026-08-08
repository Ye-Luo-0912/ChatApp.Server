namespace Core.Models.Identity;

/// <summary>
/// Durable account lifecycle state used by login issuance and request
/// authorization. A scheduled deletion is a restricted session until the
/// cooling-off deadline is reached.
/// </summary>
public enum AccountState : short
{
    Active = 0,
    DeletionPending = 1,
    Deleted = 2,
}
