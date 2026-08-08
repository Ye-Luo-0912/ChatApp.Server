using Core.Models.Auth;

namespace Core.Interfaces.Auth;

/// <summary>
/// Read boundary for the user authorization fence.
/// <para>
/// Ordinary authentication uses the L1/Garnet-backed fence. Sensitive
/// authorization explicitly uses the authoritative read. Keeping this
/// boundary separate from the snapshot mutation/store API prevents request
/// handlers from accidentally reaching for UserDbContext on the hot path.
/// </para>
/// </summary>
public interface IUserAuthorizationFence
{
    Task<UserAuthSnapshot?> GetFenceAsync(
        long userId,
        CancellationToken cancellationToken = default);

    Task<UserAuthSnapshot?> GetAuthoritativeAsync(
        long userId,
        CancellationToken cancellationToken = default);
}
