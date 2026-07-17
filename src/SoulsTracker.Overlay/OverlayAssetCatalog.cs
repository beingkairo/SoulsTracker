using System.Reflection;

namespace SoulsTracker.Overlay;

/// <summary>Provides the finite, build-time embedded assets the overlay may expose.</summary>
internal static class OverlayAssetCatalog
{
    private static readonly Dictionary<string, OverlayAsset> Assets = new(StringComparer.Ordinal)
    {
        ["overlay-bootstrap.js"] = new("SoulsTracker.Overlay.Assets.overlay-bootstrap.js", "text/javascript; charset=utf-8"),
        ["overlay-bootstrap.css"] = new("SoulsTracker.Overlay.Assets.overlay-bootstrap.css", "text/css; charset=utf-8"),
        ["souls-tracker-skull.png"] = new("SoulsTracker.Overlay.Assets.souls-tracker-skull.png", "image/png")
    };

    public static bool TryGet(string? name, out OverlayAsset asset) => Assets.TryGetValue(name ?? string.Empty, out asset!);

    internal sealed record OverlayAsset(string ResourceName, string ContentType)
    {
        public byte[] ReadBytes()
        {
            using Stream resource = typeof(OverlayAssetCatalog).Assembly.GetManifestResourceStream(ResourceName)
                ?? throw new InvalidOperationException("A required embedded overlay asset is unavailable.");
            using var output = new MemoryStream();
            resource.CopyTo(output);
            return output.ToArray();
        }
    }
}
