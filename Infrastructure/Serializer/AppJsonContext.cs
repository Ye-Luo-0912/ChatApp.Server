using System.Text.Json.Serialization;
using Core.Models.DTOs.Login;

namespace Infrastructure.Serializer;

[JsonSerializable(typeof(LoginResult))]
public partial class AppJsonContext : JsonSerializerContext
{
}