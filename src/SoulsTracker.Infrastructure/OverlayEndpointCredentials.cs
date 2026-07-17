using System.Security.Cryptography;
using System.Text;
using SoulsTracker.Application;
using SoulsTracker.Domain;

namespace SoulsTracker.Infrastructure;

/// <summary>
/// Infrastructure-owned endpoint capability. The raw token stays private here;
/// consumers can only authorize a request or ask for an explicitly approved OBS URL.
/// </summary>
public sealed class OverlayEndpointCredentials : IOverlayEndpointAccess
{
    private readonly string token;

    private OverlayEndpointCredentials(int port, string token)
    {
        Port = port;
        this.token = token;
        Configuration = new OverlayEndpointConfiguration(port, OverlayAccessToken.Parse(token));
    }

    public int Port { get; }
    public OverlayEndpointConfiguration Configuration { get; }

    public static OverlayEndpointCredentials Create(int port)
    {
        byte[] bytes = RandomNumberGenerator.GetBytes(32);
        string token = Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        return new OverlayEndpointCredentials(port, token);
    }

    public static OverlayEndpointCredentials FromConfiguration(OverlayEndpointConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        if (!configuration.IsAssigned) throw new ArgumentException("An assigned overlay endpoint is required.", nameof(configuration));
        return new OverlayEndpointCredentials(configuration.Port!.Value, configuration.AccessToken!.PersistenceValue);
    }

    public bool IsAuthorized(string? suppliedToken) =>
        suppliedToken is not null && CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(suppliedToken), Encoding.UTF8.GetBytes(token));

    public string BuildCanonicalUrl(string route)
    {
        if (route is not "/overlay/total_deaths" and not "/overlay/boss_list") throw new ArgumentException("Only approved canonical overlay routes can be displayed.", nameof(route));
        return $"http://127.0.0.1:{Port}{route}?token={token}";
    }
}

/// <summary>Infrastructure implementation factory; it is the only layer using the persistence bridge.</summary>
public sealed class OverlayEndpointAccessFactory : IOverlayEndpointAccessFactory
{
    public IOverlayEndpointAccess Create(int port) => OverlayEndpointCredentials.Create(port);
    public IOverlayEndpointAccess FromConfiguration(OverlayEndpointConfiguration configuration) => OverlayEndpointCredentials.FromConfiguration(configuration);
}
