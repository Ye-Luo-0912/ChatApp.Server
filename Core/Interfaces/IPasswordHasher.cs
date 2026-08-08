namespace Core.Interfaces;



/// <summary>

/// 密码哈希与验证（异步有界闸门；生产路径禁止同步包装）。

/// </summary>

public interface IPasswordHasher

{

    /// <summary>当前密码哈希格式与成本参数版本。</summary>
    int CurrentHashVersion { get; }

    Task<string> HashPasswordAsync(string password, CancellationToken cancellationToken = default);

    Task<bool> VerifyPasswordAsync(string password, string passwordHash, CancellationToken cancellationToken = default);

    /// <summary>判断成功验证的哈希是否应在登录后升级。</summary>
    bool NeedsRehash(string passwordHash, int storedVersion);

}


