using System.Text.Json.Serialization;
using ChatApp.Auth.Contracts;
using Core.Models.Auth;
using Core.Models.Common;
using Core.Models.Email;
using Core.Models.Identity;
using Core.Models.Presence;
using Core.Models.Token;
using Core.Models.User;
using Infrastructure.Services;

namespace Infrastructure.Serialization;

/// <summary>
/// AOT 源生成 JSON 上下文。
/// 注册的类型与应用中实际参与序列化的 DTO 保持一致。
/// <para>
/// 选项与 <see cref="AppJsonOptions.Default"/> 保持同步；
/// <c>ReadCommentHandling</c>、<c>Encoder</c>、<c>ReferenceHandler</c> 不被该特性支持，
/// 已在 <see cref="AppJsonOptions.Default"/> 中运行时配置。
/// </para>
/// </summary>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy        = JsonKnownNamingPolicy.CamelCase,
    PropertyNameCaseInsensitive = true,
    DefaultIgnoreCondition      = JsonIgnoreCondition.WhenWritingNull,
    GenerationMode              = JsonSourceGenerationMode.Default)]
// ─ 主要响应 DTO ──────────────────────────────────────────
[JsonSerializable(typeof(LoginResult))]
[JsonSerializable(typeof(TokenPairResult))]
[JsonSerializable(typeof(TokenIssueResult))]
[JsonSerializable(typeof(TokenResponse))]
[JsonSerializable(typeof(UserRegistrationResult))]
[JsonSerializable(typeof(AuthOperationResult))]
[JsonSerializable(typeof(AuthOperationError))]
[JsonSerializable(typeof(UserProfileResponse))]
[JsonSerializable(typeof(PublicUserResponse))]
// ─ 其他 ───────────────────────────────────────────────
[JsonSerializable(typeof(EmailResult))]
[JsonSerializable(typeof(AccessTokenCacheRecord))]
[JsonSerializable(typeof(RefreshToken))]
[JsonSerializable(typeof(SessionRecord))]
[JsonSerializable(typeof(UserAuthSnapshot))]
[JsonSerializable(typeof(PresenceAuthorizationProjection))]
[JsonSerializable(typeof(AttachmentDownloadTicketPayload))]
[JsonSerializable(typeof(AttachmentUploadTicket))]
[JsonSerializable(typeof(LocalAvatarStorage.AvatarTicketInfo))]
[JsonSerializable(typeof(UserStatus))]
public partial class AppJsonContext : JsonSerializerContext
{
}
