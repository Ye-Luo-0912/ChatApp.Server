namespace Core.Caching;

/// <summary>
/// Durable claim returned by a one-time state store. The original key is
/// absent while the claim key is present, so a process crash cannot silently
/// turn an in-flight credential into an untracked consumed value.
/// </summary>
public sealed record OneTimeStateClaim<T>(
    string ClaimKey,
    T Payload,
    DateTimeOffset ExpiresAt);
