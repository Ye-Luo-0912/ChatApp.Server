using System.Text.Json;
using ChatApp.Contracts.Http;
using ChatApp.Contracts.Http.Common;
using ChatApp.Contracts.Http.Friends;
using ChatApp.Contracts.Http.Sessions;
using ChatApp.Server.Controllers;
using ChatApp.Server.Models;
using Core.Models.Attachment;
using Core.Models.Friend;
using Core.Models.Token;
using Xunit;
using HttpFriendDto = ChatApp.Contracts.Http.Friends.FriendDto;

namespace ChatApp.Server.IntegrationTests.Quality;

public sealed class HttpContractBoundaryTests
{
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        TypeInfoResolver = HttpContractsJsonSerializerContext.Default,
    };

    [Fact]
    public void Core_DoesNotReferenceHttpTransportPackage()
    {
        var references = typeof(LoginResult).Assembly
            .GetReferencedAssemblies()
            .Select(name => name.Name)
            .ToArray();

        Assert.DoesNotContain("ChatApp.Contracts.Http", references);
        Assert.Contains(
            "ChatApp.Contracts.Http",
            typeof(ChatApp.Server.Program).Assembly
                .GetReferencedAssemblies()
                .Select(name => name.Name));
    }

    [Fact]
    public void CoreFriendshipLimits_MatchCanonicalHttpContract()
    {
        Assert.Equal(
            ChatApp.Contracts.Http.Friends.FriendshipInputLimits.FriendRequestMessageMaxLength,
            Core.Models.Friend.FriendshipInputLimits.FriendRequestMessageMaxLength);
        Assert.Equal(
            ChatApp.Contracts.Http.Friends.FriendshipInputLimits.FriendNoteMaxLength,
            Core.Models.Friend.FriendshipInputLimits.FriendNoteMaxLength);
    }

    [Fact]
    public void Controllers_ExposeVersionedContractsAtHttpBoundary()
    {
        var login = typeof(AuthController).GetMethod(nameof(AuthController.Login))!;
        Assert.Equal(
            typeof(ChatApp.Contracts.Http.Auth.LoginRequest),
            login.GetParameters()[0].ParameterType);

        var presign = typeof(AttachmentsController).GetMethod(nameof(AttachmentsController.Presign))!;
        Assert.Equal(
            typeof(ChatApp.Contracts.Http.Attachments.AttachmentPresignRequest),
            presign.GetParameters()[0].ParameterType);

        var sendFriendRequest = typeof(FriendshipController)
            .GetMethod(nameof(FriendshipController.SendFriendRequest))!;
        Assert.Equal(
            typeof(ChatApp.Contracts.Http.Friends.SendFriendRequestRequest),
            sendFriendRequest.GetParameters()[0].ParameterType);

        var listFriends = typeof(FriendshipController).GetMethod(nameof(FriendshipController.GetAllFriends))!;
        Assert.Equal(
            typeof(Task<CursorPage<HttpFriendDto>>),
            listFriends.ReturnType);
    }

    [Fact]
    public void RefreshResponse_PreservesCredentialAndExactExpiries()
    {
        var accessExpiry = new DateTime(2026, 8, 5, 8, 30, 0, DateTimeKind.Utc);
        var refreshExpiry = accessExpiry.AddDays(3);
        var core = TokenPairResult.Success(
            "access-token-value",
            accessExpiry,
            "refresh-token-value",
            refreshExpiry,
            "rotated-device-credential");

        var response = core.ToHttpContract();

        Assert.True(response.IsSuccess);
        Assert.Equal(accessExpiry, response.AccessTokenExpiresAtUtc);
        Assert.Equal(refreshExpiry, response.RefreshTokenExpiresAtUtc);
        Assert.Equal("rotated-device-credential", response.DeviceCredential);

        var json = JsonSerializer.Serialize(response, Json);
        Assert.Contains("\"accessTokenExpiresAtUtc\"", json, StringComparison.Ordinal);
        Assert.Contains("\"refreshTokenExpiresAtUtc\"", json, StringComparison.Ordinal);
        Assert.Contains("\"deviceCredential\"", json, StringComparison.Ordinal);
    }

    [Fact]
    public void FriendshipPageAndMutation_UseCanonicalCursorAndEnvelope()
    {
        var corePage = new Core.Models.Common.CursorPage<Core.Models.Friend.FriendDto>
        {
            Items =
            [
                new Core.Models.Friend.FriendDto
                {
                    FriendId = 42,
                    FriendName = "friend",
                    Note = "note",
                    CreatedAt = new DateTime(2026, 8, 5, 8, 0, 0, DateTimeKind.Utc),
                },
            ],
            NextCursor = "next-42",
            HasMore = true,
        };

        var page = corePage.ToHttpContract();
        var operation = FriendshipOperationResult.Success("ok").ToHttpContract();
        var envelope = new ApiEnvelope<FriendshipOperationResponse> { Data = operation };

        Assert.IsType<CursorPage<HttpFriendDto>>(page);
        Assert.Equal("next-42", page.NextCursor);
        Assert.True(page.HasMore);
        Assert.Equal(42, page.Items.Single().FriendId);

        var pageJson = JsonSerializer.Serialize(page, Json);
        var envelopeJson = JsonSerializer.Serialize(envelope, Json);
        Assert.Contains("\"items\"", pageJson, StringComparison.Ordinal);
        Assert.Contains("\"nextCursor\":\"next-42\"", pageJson, StringComparison.Ordinal);
        Assert.Contains("\"hasMore\":true", pageJson, StringComparison.Ordinal);
        Assert.Contains("\"data\"", envelopeJson, StringComparison.Ordinal);
        Assert.Contains("\"isSuccess\":true", envelopeJson, StringComparison.Ordinal);
    }

    [Fact]
    public void AttachmentPresign_PreservesRequiredUploadHeaders()
    {
        var core = new Core.Models.Attachment.AttachmentPresignResponse
        {
            AttachmentId = "attachment-1",
            UploadUrl = "https://upload.invalid/object",
            DownloadPath = "/api/attachments/attachment-1/download",
            ObjectKey = "objects/attachment-1",
            Ticket = "ticket-value",
            ExpiresAt = new DateTimeOffset(2026, 8, 5, 9, 0, 0, TimeSpan.Zero),
            UploadHeaders = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["x-amz-server-side-encryption"] = "AES256",
                ["x-amz-tagging"] = "scan-status=quarantine",
            },
        };

        var response = core.ToHttpContract();

        Assert.Equal("AES256", response.UploadHeaders!["x-amz-server-side-encryption"]);
        Assert.Equal("scan-status=quarantine", response.UploadHeaders["x-amz-tagging"]);
        var json = JsonSerializer.Serialize(response, Json);
        Assert.Contains("\"uploadHeaders\"", json, StringComparison.Ordinal);
        Assert.Contains("x-amz-server-side-encryption", json, StringComparison.Ordinal);
        Assert.Contains("x-amz-tagging", json, StringComparison.Ordinal);
    }

    [Fact]
    public void AttachmentConfirmAndSessions_MapToCanonicalWireTypes()
    {
        var confirm = new Core.Models.Attachment.ConfirmAttachmentResponse
        {
            SagaId = 7,
            AttachmentId = "attachment-7",
            DownloadPath = "/api/attachments/attachment-7/download",
            ObjectKey = "objects/attachment-7",
            Status = "Scanning",
            SagaStatus = "Requested",
        }.ToHttpContract();
        var session = new SessionDeviceProjection
        {
            DeviceId = "device-7",
            SessionId = "session-7",
            IsCurrent = true,
        }.ToHttpContract();

        Assert.IsType<ChatApp.Contracts.Http.Attachments.ConfirmAttachmentResponse>(confirm);
        Assert.Equal("attachment-7", confirm.AttachmentId);
        Assert.IsType<SessionDevice>(session);
        Assert.Equal("device-7", session.DeviceId);
        Assert.Null(typeof(LoginResult).Assembly.GetType("Core.Models.Token.SessionDeviceDto"));
    }
}
