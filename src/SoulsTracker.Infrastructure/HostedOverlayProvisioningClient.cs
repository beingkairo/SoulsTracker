using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace SoulsTracker.Infrastructure;

public enum HostedProvisioningResultKind
{
    Provisioned,
    Retry,
    Protocol,
}

public sealed record HostedProvisioningResult(HostedProvisioningResultKind Kind, string? OverlayId = null,
    TimeSpan? RetryAfter = null);
public enum HostedLegacyProbeResult { Valid, DefinitiveInvalid, Ambiguous }

/// <summary>One exact no-redirect provisioning request to the fixed approved origin.</summary>
public sealed class HostedOverlayProvisioningClient : IDisposable
{
    private const int MaximumBytes = 8192;
    private readonly string origin;
    private readonly HttpClient client;
    private readonly bool ownsClient;

    public HostedOverlayProvisioningClient(string origin, HttpMessageHandler? handler = null)
    {
        if (!Uri.TryCreate(origin, UriKind.Absolute, out Uri? uri) || uri.Scheme != Uri.UriSchemeHttps ||
            uri.GetLeftPart(UriPartial.Authority) != origin || uri.UserInfo.Length != 0)
            throw new ArgumentException("An exact HTTPS origin is required.");
        this.origin = origin;
        ownsClient = true;
        client = new HttpClient(handler ?? new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false }, disposeHandler: true)
        {
            Timeout = Timeout.InfiniteTimeSpan,
            MaxResponseContentBufferSize = MaximumBytes,
        };
    }

    public async Task<HostedProvisioningResult> ProvisionAsync(HostedProvisioningState state,
        CancellationToken cancellationToken = default)
    {
        if (state.Origin != origin || state.IsPreReleaseVersion1) return new(HostedProvisioningResultKind.Protocol);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, $"{origin}/api/v1/overlays");
            request.Headers.CacheControl = new CacheControlHeaderValue { NoStore = true };
            byte[] body = JsonSerializer.SerializeToUtf8Bytes(new
            {
                v = 1,
                requestId = state.RequestId,
                readVerifier = state.ReadVerifier,
                writeVerifier = state.WriteVerifier,
            });
            request.Content = new ByteArrayContent(body);
            request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
            using HttpResponseMessage response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead,
                timeout.Token).ConfigureAwait(false);
            if (response.StatusCode == HttpStatusCode.OK)
            {
                byte[] bytes = await ReadBoundedAsync(response.Content, timeout.Token).ConfigureAwait(false);
                try
                {
                    using var document = JsonDocument.Parse(bytes, new() { MaxDepth = 2 });
                    JsonElement root = document.RootElement;
                    if (root.ValueKind != JsonValueKind.Object || root.EnumerateObject().Count() != 3 ||
                        !root.TryGetProperty("v", out JsonElement version) ||
                        !root.TryGetProperty("status", out JsonElement status) ||
                        !root.TryGetProperty("overlayId", out JsonElement overlayId) ||
                        version.ValueKind != JsonValueKind.Number || !version.TryGetInt32(out int parsedVersion) || parsedVersion != 1 ||
                        status.ValueKind != JsonValueKind.String || status.GetString() != "provisioned" ||
                        overlayId.ValueKind != JsonValueKind.String || !HostedPublisherConfiguration.Hex(overlayId.GetString(), 64))
                        return new(HostedProvisioningResultKind.Protocol);
                    return new(HostedProvisioningResultKind.Provisioned, overlayId.GetString());
                }
                catch (JsonException) { return new(HostedProvisioningResultKind.Protocol); }
                finally { System.Security.Cryptography.CryptographicOperations.ZeroMemory(bytes); }
            }
            if (response.StatusCode == HttpStatusCode.Conflict || response.StatusCode == HttpStatusCode.RequestTimeout ||
                response.StatusCode == HttpStatusCode.TooManyRequests ||
                (int)response.StatusCode >= 500)
                return new(HostedProvisioningResultKind.Retry, RetryAfter: RetryAfter(response));
            return new(HostedProvisioningResultKind.Protocol);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new(HostedProvisioningResultKind.Retry);
        }
        catch (HttpRequestException)
        {
            return new(HostedProvisioningResultKind.Retry);
        }
        catch (IOException)
        {
            return new(HostedProvisioningResultKind.Retry);
        }
    }

    public async Task<HostedLegacyProbeResult> ProbePreReleaseAsync(HostedPublisherConfiguration configuration,
        CancellationToken cancellationToken = default)
    {
        if (configuration.Version != 1 || configuration.DisplayOrigin != origin) return HostedLegacyProbeResult.DefinitiveInvalid;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get,
                $"{origin}/api/v1/overlays/{configuration.OverlayId}/publisher");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", configuration.WriteCapability);
            request.Headers.CacheControl = new CacheControlHeaderValue { NoStore = true };
            using HttpResponseMessage response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead,
                timeout.Token).ConfigureAwait(false);
            if (response.StatusCode == HttpStatusCode.OK) return HostedLegacyProbeResult.Valid;
            if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden or HttpStatusCode.NotFound)
                return HostedLegacyProbeResult.DefinitiveInvalid;
            return HostedLegacyProbeResult.Ambiguous;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { return HostedLegacyProbeResult.Ambiguous; }
        catch (HttpRequestException) { return HostedLegacyProbeResult.Ambiguous; }
        catch (IOException) { return HostedLegacyProbeResult.Ambiguous; }
    }

    private static TimeSpan? RetryAfter(HttpResponseMessage response)
    {
        TimeSpan? value = response.Headers.RetryAfter?.Delta;
        return value.HasValue && value.Value > TimeSpan.Zero && value.Value <= TimeSpan.FromSeconds(60) ? value : null;
    }

    private static async Task<byte[]> ReadBoundedAsync(HttpContent content, CancellationToken cancellationToken)
    {
        await using Stream stream = await content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        byte[] buffer = new byte[MaximumBytes + 1];
        int used = 0;
        while (used < buffer.Length)
        {
            int read = await stream.ReadAsync(buffer.AsMemory(used), cancellationToken).ConfigureAwait(false);
            if (read == 0) break;
            used += read;
        }
        if (used > MaximumBytes) throw new InvalidDataException();
        return buffer[..used];
    }

    public void Dispose()
    {
        if (ownsClient) client.Dispose();
    }
}
