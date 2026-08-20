using SoulsTracker.Desktop;

namespace SoulsTracker.Desktop.Tests;

public sealed class OverlayHostStartupRegistrationTests
{
    [Fact]
    public void OptInWritesOnlyTheCurrentUserRunEntryAndDisableRemovesIt()
    {
        var runKey = new MemoryRunKey();
        var registration = new OverlayHostStartupRegistration(@"C:\Program Files\SoulsTracker\SoulsTracker.OverlayHost.exe", runKey);

        Assert.False(registration.IsEnabled);
        registration.Enable();
        Assert.True(registration.IsEnabled);
        Assert.Equal("\"C:\\Program Files\\SoulsTracker\\SoulsTracker.OverlayHost.exe\"", runKey.Value);
        registration.Disable();
        Assert.False(registration.IsEnabled);
        Assert.Null(runKey.Value);
    }

    private sealed class MemoryRunKey : IOverlayHostRunKey
    {
        public string? Value { get; private set; }
        public string? Read(string name) => Value;
        public void Write(string name, string commandLine) => Value = commandLine;
        public void Remove(string name) => Value = null;
    }
}
