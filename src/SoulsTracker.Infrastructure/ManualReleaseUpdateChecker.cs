using System.Net;
using System.Text.Json;

namespace SoulsTracker.Infrastructure;

/// <summary>Checks SoulsTracker's public GitHub releases on request or after explicit startup opt-in.</summary>
public interface IManualReleaseUpdateChecker
{
    ValueTask<ManualReleaseUpdateResult> CheckAsync(string installedVersion, CancellationToken cancellationToken = default);
}

public enum ManualReleaseUpdateStatus { UpToDate, UpdateAvailable, InvalidInstalledVersion, InvalidResponse, RateLimited, Unavailable }

public sealed record ManualReleaseUpdateResult(ManualReleaseUpdateStatus Status, string? AvailableVersion = null, Uri? ReleasePage = null);

/// <summary>Semantic version parser/comparer for local versions and GitHub release tags.</summary>
public sealed class ReleaseSemanticVersion : IComparable<ReleaseSemanticVersion>
{
    private ReleaseSemanticVersion(int major, int minor, int patch, string[] prerelease) => (Major, Minor, Patch, Prerelease) = (major, minor, patch, prerelease);
    public int Major { get; }
    public int Minor { get; }
    public int Patch { get; }
    public IReadOnlyList<string> Prerelease { get; }

    public static bool TryParse(string? input, out ReleaseSemanticVersion? version)
    {
        version = null;
        if (string.IsNullOrWhiteSpace(input) || input.Length > 128) return false;
        string value = input.Trim();
        if (value.StartsWith('v')) value = value[1..];
        string[] metadata = value.Split('+', 2);
        string[] parts = metadata[0].Split('-', 2);
        string[] core = parts[0].Split('.');
        if (core.Length != 3 || !TryNumeric(core[0], out int major) || !TryNumeric(core[1], out int minor) || !TryNumeric(core[2], out int patch)) return false;
        string[] prerelease = parts.Length == 1 ? [] : parts[1].Split('.');
        if (prerelease.Any(static item => item.Length == 0 || item.Any(static character => !char.IsAsciiLetterOrDigit(character) && character != '-') || (item.Length > 1 && item[0] == '0' && item.All(char.IsAsciiDigit)))) return false;
        version = new ReleaseSemanticVersion(major, minor, patch, prerelease);
        return true;
    }

    public int CompareTo(ReleaseSemanticVersion? other)
    {
        if (other is null) return 1;
        int core = Major.CompareTo(other.Major); if (core != 0) return core;
        core = Minor.CompareTo(other.Minor); if (core != 0) return core;
        core = Patch.CompareTo(other.Patch); if (core != 0) return core;
        if (Prerelease.Count == 0 || other.Prerelease.Count == 0) return Prerelease.Count == other.Prerelease.Count ? 0 : Prerelease.Count == 0 ? 1 : -1;
        for (int index = 0; index < Math.Min(Prerelease.Count, other.Prerelease.Count); index++)
        {
            string left = Prerelease[index], right = other.Prerelease[index];
            bool leftNumeric = int.TryParse(left, out int leftNumber), rightNumeric = int.TryParse(right, out int rightNumber);
            int value = leftNumeric && rightNumeric ? leftNumber.CompareTo(rightNumber) : leftNumeric ? -1 : rightNumeric ? 1 : string.CompareOrdinal(left, right);
            if (value != 0) return value;
        }
        return Prerelease.Count.CompareTo(other.Prerelease.Count);
    }

    public override bool Equals(object? obj) => obj is ReleaseSemanticVersion other && CompareTo(other) == 0;
    public override int GetHashCode() => HashCode.Combine(Major, Minor, Patch, string.Join('.', Prerelease));
    public static bool operator ==(ReleaseSemanticVersion? left, ReleaseSemanticVersion? right) => Equals(left, right);
    public static bool operator !=(ReleaseSemanticVersion? left, ReleaseSemanticVersion? right) => !Equals(left, right);
    public static bool operator <(ReleaseSemanticVersion left, ReleaseSemanticVersion right) => left.CompareTo(right) < 0;
    public static bool operator <=(ReleaseSemanticVersion left, ReleaseSemanticVersion right) => left.CompareTo(right) <= 0;
    public static bool operator >(ReleaseSemanticVersion left, ReleaseSemanticVersion right) => left.CompareTo(right) > 0;
    public static bool operator >=(ReleaseSemanticVersion left, ReleaseSemanticVersion right) => left.CompareTo(right) >= 0;
    public override string ToString() => $"{Major}.{Minor}.{Patch}" + (Prerelease.Count == 0 ? string.Empty : $"-{string.Join('.', Prerelease)}");
    private static bool TryNumeric(string value, out int number)
    {
        number = 0;
        return !(value.Length > 1 && value[0] == '0') && int.TryParse(value, out number) && number >= 0;
    }
}

public sealed class GitHubLatestReleaseUpdateChecker(HttpClient client) : IManualReleaseUpdateChecker
{
    private static readonly Uri LatestReleaseEndpoint = new("https://api.github.com/repos/beingkairo/SoulsTracker/releases/latest");
    private static readonly Uri ReleasesPage = new("https://github.com/beingkairo/SoulsTracker/releases");
    private readonly HttpClient client = client ?? throw new ArgumentNullException(nameof(client));

    public async ValueTask<ManualReleaseUpdateResult> CheckAsync(string installedVersion, CancellationToken cancellationToken = default)
    {
        if (!ReleaseSemanticVersion.TryParse(installedVersion, out ReleaseSemanticVersion? installed)) return new(ManualReleaseUpdateStatus.InvalidInstalledVersion);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, LatestReleaseEndpoint);
            request.Headers.UserAgent.ParseAdd("SoulsTracker-manual-update-check");
            using HttpResponseMessage response = await client.SendAsync(request, timeout.Token).ConfigureAwait(false);
            if (response.StatusCode is HttpStatusCode.TooManyRequests or HttpStatusCode.Forbidden) return new(ManualReleaseUpdateStatus.RateLimited);
            if (!response.IsSuccessStatusCode) return new(ManualReleaseUpdateStatus.Unavailable);
            using JsonDocument json = JsonDocument.Parse(await response.Content.ReadAsStreamAsync(timeout.Token).ConfigureAwait(false));
            if (json.RootElement.ValueKind != JsonValueKind.Object || !json.RootElement.TryGetProperty("tag_name", out JsonElement tag) || tag.ValueKind != JsonValueKind.String || !ReleaseSemanticVersion.TryParse(tag.GetString(), out ReleaseSemanticVersion? released)) return new(ManualReleaseUpdateStatus.InvalidResponse);
            if (released!.CompareTo(installed!) <= 0) return new(ManualReleaseUpdateStatus.UpToDate, released.ToString());
            Uri page = ReleasesPage;
            if (json.RootElement.TryGetProperty("html_url", out JsonElement url) && url.ValueKind == JsonValueKind.String && Uri.TryCreate(url.GetString(), UriKind.Absolute, out Uri? candidate) && candidate.Scheme == Uri.UriSchemeHttps && candidate.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase)) page = candidate;
            return new(ManualReleaseUpdateStatus.UpdateAvailable, released.ToString(), page);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { return new(ManualReleaseUpdateStatus.Unavailable); }
        catch (HttpRequestException) { return new(ManualReleaseUpdateStatus.Unavailable); }
        catch (JsonException) { return new(ManualReleaseUpdateStatus.InvalidResponse); }
    }
}
