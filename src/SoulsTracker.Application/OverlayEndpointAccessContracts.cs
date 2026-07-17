using SoulsTracker.Domain;

namespace SoulsTracker.Application;

/// <summary>
/// Opaque local-overlay endpoint capability. Application consumers can authorize
/// a supplied value or request an approved canonical URL, never read a token.
/// </summary>
public interface IOverlayEndpointAccess
{
    OverlayEndpointConfiguration Configuration { get; }
    bool IsAuthorized(string? suppliedToken);
    string BuildCanonicalUrl(string route);
}

/// <summary>Creates an opaque endpoint capability from new or persisted configuration.</summary>
public interface IOverlayEndpointAccessFactory
{
    IOverlayEndpointAccess Create(int port);
    IOverlayEndpointAccess FromConfiguration(OverlayEndpointConfiguration configuration);
}
