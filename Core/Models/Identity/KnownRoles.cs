namespace Core.Models.Identity;

/// <summary>可分配角色白名单（禁止接口动态创建任意角色）。</summary>
public static class KnownRoles
{
    public const string Admin = "Admin";
    public const string User = "User";

    public static readonly IReadOnlyList<string> All = [Admin, User];

    public static bool IsAssignable(string? roleName) =>
        !string.IsNullOrWhiteSpace(roleName)
        && All.Any(r => string.Equals(r, roleName.Trim(), StringComparison.OrdinalIgnoreCase));

    public static string Normalize(string roleName) => roleName.Trim();
}
