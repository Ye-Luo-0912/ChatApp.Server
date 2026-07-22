namespace Core.Interfaces;



/// <summary>

/// 密码哈希与验证（异步有界闸门；生产路径禁止同步包装）。

/// </summary>

public interface IPasswordHasher

{

    Task<string> HashPasswordAsync(string password, CancellationToken cancellationToken = default);

    Task<bool> VerifyPasswordAsync(string password, string passwordHash, CancellationToken cancellationToken = default);

}


