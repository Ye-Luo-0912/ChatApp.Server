namespace Core.Interfaces;

/// <summary>短信供应商边界；业务层不记录手机号验证码。</summary>
public interface IPhoneVerificationSender
{
    Task<bool> SendAsync(
        string e164PhoneNumber,
        string code,
        CancellationToken cancellationToken = default);
}
