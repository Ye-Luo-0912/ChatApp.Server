namespace Core.Models.DTOs.Login;

public struct ServerEndPoint
{
    public string Host { get; set; }
    public string Name {get;set; }
    public ushort Port { get; set; }
}