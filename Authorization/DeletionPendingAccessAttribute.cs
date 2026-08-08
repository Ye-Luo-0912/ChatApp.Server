namespace ChatApp.Server.Authorization;

/// <summary>
/// Marks an endpoint that remains available to a session during the account
/// deletion cooling-off period. All other authenticated endpoints are denied
/// by <see cref="DeletionPendingAccessMiddleware"/>.
/// </summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method, AllowMultiple = false)]
public sealed class DeletionPendingAccessAttribute : Attribute;
