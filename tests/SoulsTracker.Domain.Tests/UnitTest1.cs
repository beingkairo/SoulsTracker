using SoulsTracker.Domain;

namespace SoulsTracker.Domain.Tests;

public class AssemblyBoundaryTests
{
    [Fact]
    public void DomainAssemblyMarkerIsAvailable()
    {
        Assert.Equal("SoulsTracker.Domain", typeof(DomainAssemblyMarker).Namespace);
    }
}
