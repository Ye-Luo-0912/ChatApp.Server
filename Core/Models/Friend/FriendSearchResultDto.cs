    namespace Core.Models.Friend;

    public class FriendSearchResultDto
    {
        public long FriendId { get; set; }
        public string? FriendName { get; set; } = string.Empty;
        public string? Note { get; set; } = string.Empty;
        public DateTimeOffset? LastInteractionAt { get; set; }
        public int RelevanceScore { get; set; }
    }
