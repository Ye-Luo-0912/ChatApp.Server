namespace Core.Models.Identity;

public class ApplicationRoles
{
    public long Id { get; set; }
    public string? Name { get; set; }
    public string? NormalizedName { get; set; }
    public string? ConcurrencyStamp { get; set; } = Guid.NewGuid().ToString();
}