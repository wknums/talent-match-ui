using System.Net;
using System.Text.Json;
using Microsoft.JSInterop;

namespace TalentMatch.Web.Client.Services;

/// <summary>
/// Discards the cached access token so the next acquisition performs a silent network refresh.
/// </summary>
public interface IAccessTokenCacheInvalidator
{
    ValueTask InvalidateAsync(CancellationToken cancellationToken = default);
}

/// <inheritdoc />
public sealed class MsalAccessTokenCacheInvalidator(IJSRuntime jsRuntime) : IAccessTokenCacheInvalidator
{
    public async ValueTask InvalidateAsync(CancellationToken cancellationToken = default)
        => await jsRuntime.InvokeVoidAsync("talentMatchPurgeCachedAccessTokens", cancellationToken);
}

/// <summary>
/// Retries a request once when the API rejects an otherwise valid token for being older than the
/// authorization freshness window. The server enforces that window on every request, while MSAL
/// caches access tokens for their full lifetime, so a cached token becomes stale long before it
/// expires. Dropping the cached token forces a silent renewal; if the session can no longer be
/// renewed, the authorization handler raises the usual interactive sign-in requirement.
/// Only idempotent methods are retried, matching the Stack A transport.
/// </summary>
public sealed class StaleTokenRetryHandler(IAccessTokenCacheInvalidator invalidator) : DelegatingHandler
{
    private const string StaleTokenErrorCode = "token_stale";

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        var response = await base.SendAsync(request, cancellationToken);

        if (response.StatusCode != HttpStatusCode.Unauthorized || !IsIdempotent(request.Method))
            return response;

        if (!await IsStaleTokenAsync(response, cancellationToken))
            return response;

        try
        {
            await invalidator.InvalidateAsync(cancellationToken);
        }
        catch (JSException)
        {
            // The cached token could not be discarded, so a retry would replay the same stale
            // token. Surface the original denial instead of failing the whole request pipeline.
            return response;
        }

        response.Dispose();

        var retry = CloneWithoutAuthorization(request);
        return await base.SendAsync(retry, cancellationToken);
    }

    private static bool IsIdempotent(HttpMethod method)
        => method == HttpMethod.Get || method == HttpMethod.Head || method == HttpMethod.Options;

    private static async Task<bool> IsStaleTokenAsync(
        HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        try
        {
            // ReadAsStringAsync buffers the content, so the body stays readable for the caller
            // when the failure turns out to be something other than a stale token.
            var payload = await response.Content.ReadAsStringAsync(cancellationToken);
            if (string.IsNullOrWhiteSpace(payload))
                return false;

            using var document = JsonDocument.Parse(payload);
            return document.RootElement.ValueKind == JsonValueKind.Object
                && document.RootElement.TryGetProperty("error", out var error)
                && error.ValueKind == JsonValueKind.String
                && string.Equals(error.GetString(), StaleTokenErrorCode, StringComparison.Ordinal);
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static HttpRequestMessage CloneWithoutAuthorization(HttpRequestMessage request)
    {
        // Only idempotent requests reach this path, so there is no body to rewind.
        var clone = new HttpRequestMessage(request.Method, request.RequestUri)
        {
            Version = request.Version,
            VersionPolicy = request.VersionPolicy,
        };

        foreach (var header in request.Headers)
        {
            if (string.Equals(header.Key, "Authorization", StringComparison.OrdinalIgnoreCase))
                continue;

            clone.Headers.TryAddWithoutValidation(header.Key, header.Value);
        }

        foreach (var option in request.Options)
        {
            ((IDictionary<string, object?>)clone.Options)[option.Key] = option.Value;
        }

        return clone;
    }
}
