namespace Core.Models.Email;

public enum EmailOutboxStatus
{
    Pending = 0,
    Processing = 1,
    Sent = 2,
    Failed = 3,
    Dead = 4,
}
