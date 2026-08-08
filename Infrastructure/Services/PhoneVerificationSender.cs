using System.Net.Http.Json;
using Core.Interfaces;
using Core.Settings;
using Microsoft.Extensions.Options;

namespace Infrastructure.Services;

/// <summary>可对接短信供应商的 HTTPS webhook；没有配置时安全失败，不会伪造已发送。</summary>
public sealed class PhoneVerificationSender(
    IHttpClientFactory clients,
    IOptions<PhoneVerificationOptions> options) : IPhoneVerificationSender
{
    public async Task<bool> SendAsync(
        string e164PhoneNumber,
        string code,
        CancellationToken cancellationToken = default)
    {
        var opts = options.Value;
        if (!Uri.TryCreate(opts.WebhookUrl, UriKind.Absolute, out var uri)
            || uri.Scheme != Uri.UriSchemeHttps)
            return false;

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, uri)
            {
                Content = JsonContent.Create(new
                {
                    phoneNumber = e164PhoneNumber,
                    code,
                    purpose = "change-phone",
                }),
            };
            if (!string.IsNullOrWhiteSpace(opts.AuthorizationToken))
                request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue(
                    "Bearer", opts.AuthorizationToken);

            using var response = await clients.CreateClient("phone-verification")
                .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                .ConfigureAwait(false);
            return response.IsSuccessStatusCode;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return false;
        }
    }
}
