namespace SoulsTracker.Domain;

/// <summary>Validated local-only configuration for the optional desktop death sound.</summary>
public sealed record DeathSoundConfiguration
{
    public static DeathSoundConfiguration Default { get; } = new(null, isEnabled: false, volume: 100);

    public DeathSoundConfiguration(string? localPath, bool isEnabled, int volume)
    {
        if (volume is < 0 or > 100)
        {
            throw new ArgumentOutOfRangeException(nameof(volume), "Death sound volume must be between 0 and 100.");
        }

        if (!string.IsNullOrWhiteSpace(localPath))
        {
            string extension = Path.GetExtension(localPath);
            if (!extension.Equals(".wav", StringComparison.OrdinalIgnoreCase) &&
                !extension.Equals(".mp3", StringComparison.OrdinalIgnoreCase))
            {
                throw new ArgumentException("Death sound must be a WAV or MP3 file.", nameof(localPath));
            }
        }

        LocalPath = string.IsNullOrWhiteSpace(localPath) ? null : localPath;
        IsEnabled = isEnabled;
        Volume = volume;
    }

    /// <summary>Internal persisted local path. Desktop UI must never display or diagnose it.</summary>
    public string? LocalPath { get; }
    public bool IsEnabled { get; }
    public int Volume { get; }
}
