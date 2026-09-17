using System.Reflection;
using System.Text.Json;
using SoulsTracker.Domain;

namespace SoulsTracker.Domain.Tests;

public sealed class OverlaySnapshotTests
{
    [Fact]
    public void SnapshotSerializesTotalDeathsWithoutSecretsOrMutationCapabilities()
    {
        OverlaySnapshot snapshot = new(
            OverlaySnapshot.CurrentSchemaVersion,
            sequenceNumber: 1,
            new DateTimeOffset(2026, 7, 11, 12, 0, 0, TimeSpan.Zero),
            new OverlayGameMetadata(GameId.Ds1),
            TotalDeathsDisplayValue.FromUnavailableSelectedGame(GameId.Ds1));

        string json = JsonSerializer.Serialize(snapshot);
        PropertyInfo[] properties = typeof(OverlaySnapshot).GetProperties(BindingFlags.Public | BindingFlags.Instance);
        MethodInfo[] mutationMethods = typeof(OverlaySnapshot).GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly).Where(static method => !method.IsSpecialName).ToArray();

        Assert.Contains("\"SchemaVersion\":1", json);
        Assert.DoesNotContain("\"AccessToken\"", json);
        Assert.DoesNotContain("\"Endpoint\"", json);
        Assert.DoesNotContain(properties, static property => property.PropertyType == typeof(OverlayConfiguration));
        Assert.Empty(mutationMethods);
    }
}
