namespace Core.Exceptions;

/// <summary>BCrypt 闸门过载：调用方应快速失败并映射为 503。</summary>
public sealed class PasswordVerifyOverloadedException : Exception
{
    public PasswordVerifyOverloadedException()
        : base("密码校验过载，请稍后重试")
    {
    }
}
