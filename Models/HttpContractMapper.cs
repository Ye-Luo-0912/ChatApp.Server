using ChatApp.Contracts.Http.Attachments;
using ChatApp.Contracts.Http.Auth;
using ChatApp.Contracts.Http.Common;
using ChatApp.Contracts.Http.Friends;
using ChatApp.Contracts.Http.Sessions;
using Core.Models.Auth;
using Core.Models.Token;
using CoreFriendDto = Core.Models.Friend.FriendDto;
using CoreFriendRequestDto = Core.Models.Friend.FriendRequestDto;
using CoreBlockedUserDto = Core.Models.Friend.BlockedUserDto;
using CoreFriendshipOperationResult = Core.Models.Friend.FriendshipOperationResult;
using CoreSendFriendRequestResult = Core.Models.Friend.SendFriendRequestResult;
using CoreAttachmentPresignRequest = Core.Models.Attachment.AttachmentPresignRequest;
using CoreAttachmentPresignResponse = Core.Models.Attachment.AttachmentPresignResponse;
using CoreConfirmAttachmentRequest = Core.Models.Attachment.ConfirmAttachmentRequest;
using CoreConfirmAttachmentResponse = Core.Models.Attachment.ConfirmAttachmentResponse;
using CoreSessionDeviceProjection = Core.Models.Token.SessionDeviceProjection;

namespace ChatApp.Server.Models;

/// <summary>
/// Maps BCL-only Core projections to the versioned HTTP wire contract. Keeping
/// this adapter in the Host prevents transport packages from leaking into Core.
/// </summary>
internal static class HttpContractMapper
{
    public static LoginResponse ToHttpContract(this LoginResult source) => new()
    {
        IsSuccess = source.IsSuccess,
        LoginCheckStatus = (ChatApp.Contracts.Http.Auth.LoginCheckStatus)source.LoginCheckStatus,
        ErrorMessage = source.ErrorMessage,
        AccessToken = source.AccessToken,
        AccessTokenExpiresAtUtc = source.AccessTokenExpiresAtUtc,
        RefreshToken = source.RefreshToken,
        RefreshTokenExpiresAtUtc = source.RefreshTokenExpiresAtUtc,
        LoginAt = source.LoginAt,
        PreviousLoginDate = source.PreviousLoginDate,
        ClientIp = source.ClientIp,
        IsNewDevice = source.IsNewDevice,
        IsUnusualLocation = source.IsUnusualLocation,
        TrustedDeviceToken = source.TrustedDeviceToken,
        DeviceCredential = source.DeviceCredential,
        RequiresRecoveryCodeRegeneration = source.RequiresRecoveryCodeRegeneration,
        SessionId = source.SessionId,
        DeviceIdHash = source.DeviceIdHash,
        MfaToken = source.MfaToken,
        RequiresTwoFactor = source.RequiresTwoFactor,
        UserId = source.UserId,
        UserName = source.UserName,
        Email = source.Email,
        AvatarUrl = source.AvatarUrl,
        Signature = source.Signature,
        Gender = source.Gender,
        Region = source.Region,
        Status = (UserPresenceStatus)source.Status,
        AccountState = (AccountLifecycleState)source.AccountState,
        DeletionScheduledAt = source.DeletionScheduledAt,
        Server = source.Server is { } server
            ? new ServerEndpoint
            {
                Host = server.Host,
                Name = server.Name,
                Port = server.Port,
            }
            : null,
    };

    public static RefreshTokenResponse ToHttpContract(this TokenPairResult source) => new()
    {
        IsSuccess = source.IsSuccess,
        AccessToken = source.AccessToken,
        AccessTokenExpiresAtUtc = source.AccessTokenExpiresAtUtc,
        RefreshToken = source.RefreshToken,
        RefreshTokenExpiresAtUtc = source.RefreshTokenExpiresAtUtc,
        DeviceCredential = source.DeviceCredential,
        ErrorType = source.ErrorType is { } error
            ? (ChatApp.Contracts.Http.Auth.AuthErrorType)error
            : null,
    };

    public static RegisterResponse ToHttpContract(this UserRegistrationResult source) => new()
    {
        IsSuccess = source.IsSuccess,
        UserId = source.UserId,
        Username = source.Username,
        Message = source.Message,
        Errors = source.Errors.Count == 0
            ? null
            : source.Errors.Select(error => new RegistrationError
            {
                Code = error.Code,
                Description = error.Description,
            }).ToArray(),
    };

    public static ChatApp.Contracts.Http.Auth.EmailResult ToHttpContract(this Core.Models.Email.EmailResult source) => new()
    {
        IsSuccess = source.IsSuccess,
        ErrorMessage = source.ErrorMessage,
    };

    public static CoreAttachmentPresignRequest ToCoreContract(this AttachmentPresignRequest source) => new()
    {
        ContentType = source.ContentType,
        ContentLength = source.ContentLength,
        OriginalName = source.OriginalName,
        ClientAttachmentId = source.ClientAttachmentId,
        Sha256 = source.Sha256,
    };

    public static AttachmentPresignResponse ToHttpContract(this CoreAttachmentPresignResponse source) => new()
    {
        AttachmentId = source.AttachmentId,
        UploadUrl = source.UploadUrl,
        DownloadPath = source.DownloadPath,
        ObjectKey = source.ObjectKey,
        Ticket = source.Ticket,
        ExpiresAt = source.ExpiresAt,
        Deduplicated = source.Deduplicated,
        UploadHeaders = source.UploadHeaders,
    };

    public static CoreConfirmAttachmentRequest ToCoreContract(this ConfirmAttachmentRequest source) => new()
    {
        ObjectKey = source.ObjectKey,
        Ticket = source.Ticket,
        AttachmentId = source.AttachmentId,
    };

    public static ConfirmAttachmentResponse ToHttpContract(this CoreConfirmAttachmentResponse source) => new()
    {
        SagaId = source.SagaId,
        AttachmentId = source.AttachmentId,
        DownloadPath = source.DownloadPath,
        ObjectKey = source.ObjectKey,
        Status = source.Status,
        SagaStatus = source.SagaStatus,
    };

    public static SessionDevice ToHttpContract(this CoreSessionDeviceProjection source) => new()
    {
        DeviceId = source.DeviceId,
        DeviceName = source.DeviceName,
        DeviceType = source.DeviceType,
        ClientIp = source.ClientIp,
        UserAgent = source.UserAgent,
        LoginAt = source.LoginAt,
        LastActiveAt = source.LastActiveAt,
        ExpiresAt = source.ExpiresAt,
        SessionId = source.SessionId,
        RefreshCount = source.RefreshCount,
        IsCurrent = source.IsCurrent,
    };

    public static CursorPage<FriendDto> ToHttpContract(
        this Core.Models.Common.CursorPage<CoreFriendDto> source) => new()
    {
        Items = source.Items.Select(ToHttpContract).ToArray(),
        NextCursor = source.NextCursor,
        HasMore = source.HasMore,
    };

    public static CursorPage<FriendRequestDto> ToHttpContract(
        this Core.Models.Common.CursorPage<CoreFriendRequestDto> source) => new()
    {
        Items = source.Items.Select(ToHttpContract).ToArray(),
        NextCursor = source.NextCursor,
        HasMore = source.HasMore,
    };

    public static CursorPage<BlockedUserDto> ToHttpContract(
        this Core.Models.Common.CursorPage<CoreBlockedUserDto> source) => new()
    {
        Items = source.Items.Select(ToHttpContract).ToArray(),
        NextCursor = source.NextCursor,
        HasMore = source.HasMore,
    };

    public static CursorPage<T> ToHttpContract<T>(
        this Core.Models.Common.CursorPage<T> source) => new()
    {
        Items = source.Items,
        NextCursor = source.NextCursor,
        HasMore = source.HasMore,
    };

    public static FriendDto ToHttpContract(this CoreFriendDto source) => new()
    {
        FriendId = source.FriendId,
        FriendName = source.FriendName,
        Note = source.Note,
        CreatedAt = source.CreatedAt,
        AvatarUrl = source.AvatarUrl,
        GroupId = source.GroupId,
        GroupName = source.GroupName,
        LastInteractionAt = source.LastInteractionAt,
        LastSeenAt = source.LastSeenAt,
    };

    public static FriendRequestDto ToHttpContract(this CoreFriendRequestDto source) => new()
    {
        RequestId = source.RequestId,
        RequesterId = source.RequesterId,
        TargetUserId = source.TargetUserId,
        Message = source.Message,
        Status = (FriendRequestStatus)source.Status,
        CreatedAt = source.CreatedAt,
    };

    public static BlockedUserDto ToHttpContract(this CoreBlockedUserDto source) => new()
    {
        UserId = source.UserId,
        UserName = source.UserName,
        AvatarUrl = source.AvatarUrl,
        BlockedAt = source.BlockedAt,
    };

    public static FriendshipOperationResponse ToHttpContract(
        this CoreFriendshipOperationResult source) => new()
    {
        IsSuccess = source.IsSuccess,
        ErrorCode = (FriendshipOperationErrorCode)source.ErrorCode,
        Message = source.Message,
    };

    public static SendFriendRequestResponse ToHttpContract(
        this CoreSendFriendRequestResult source) => new()
    {
        IsSuccess = source.IsSuccess,
        ErrorCode = (FriendshipOperationErrorCode)source.ErrorCode,
        Message = source.Message,
        Outcome = (ChatApp.Contracts.Http.Friends.SendFriendRequestOutcome)source.Outcome,
        Friend = source.Friend?.ToHttpContract(),
    };

    public static FriendshipGenericOperationResponse<FriendDto> ToHttpContract(
        this Core.Models.Friend.FriendshipOperationResult<CoreFriendDto> source) => new()
    {
        Succeeded = source.Succeeded,
        ErrorCode = (FriendshipOperationErrorCode)source.ErrorCode,
        Message = source.Message,
        Data = source.Data?.ToHttpContract(),
    };

    public static FriendshipGenericOperationResponse<T> ToHttpContract<T>(
        this Core.Models.Friend.FriendshipOperationResult<T> source) => new()
    {
        Succeeded = source.Succeeded,
        ErrorCode = (FriendshipOperationErrorCode)source.ErrorCode,
        Message = source.Message,
        Data = source.Data,
    };
}
