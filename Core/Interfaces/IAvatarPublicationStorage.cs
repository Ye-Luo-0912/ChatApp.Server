namespace Core.Interfaces;

/// <summary>
/// Publishes an already-written avatar candidate after the UserDb transaction
/// has established the durable AvatarUrl reference. Implementations must be
/// idempotent; a provider without object tags can implement this as a no-op.
/// </summary>
public interface IAvatarPublicationStorage
{
    Task PublishAsync(string objectKey, CancellationToken cancellationToken = default);
}
