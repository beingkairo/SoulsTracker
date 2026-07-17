using SoulsTracker.Domain;

namespace SoulsTracker.Domain.Tests;

public sealed class DeathSoundConfigurationTests
{
    [Fact]
    public void DefaultsAndSupportedExtensionsAreBounded()
    {
        Assert.Equal(100, DeathSoundConfiguration.Default.Volume);
        Assert.False(DeathSoundConfiguration.Default.IsEnabled);
        Assert.Null(DeathSoundConfiguration.Default.LocalPath);
        Assert.True(new DeathSoundConfiguration("C:\\safe\\death.wav", true, 0).IsEnabled);
        Assert.True(new DeathSoundConfiguration("C:\\safe\\death.MP3", true, 100).IsEnabled);
        Assert.Throws<ArgumentException>(() => new DeathSoundConfiguration("C:\\safe\\death.ogg", true, 100));
        Assert.Throws<ArgumentOutOfRangeException>(() => new DeathSoundConfiguration(null, true, 101));
    }
}
