namespace Core.Models.Friend;

/// <summary>Shared API and persistence bounds for user-provided friendship text.</summary>
public static class FriendshipInputLimits
{
    /// <summary>Maximum characters in an optional friend-request message.</summary>
    public const int FriendRequestMessageMaxLength = 500;

    /// <summary>Maximum characters in a per-friend display note.</summary>
    public const int FriendNoteMaxLength = 100;
}
