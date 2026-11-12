using System;
using System.Globalization;
// The wire format is shared with the game runtime, which targets .NET Framework.
#pragma warning disable CA1850, CA1872
using System.Security.Cryptography;
using System.Text;

namespace Soulstracker.DeathTracking;

/// <summary>The single versioned, checksummed record exchanged between the plugin and desktop.</summary>
public sealed class DeathRecord
{
    public DeathRecord(Guid installation, Guid run, int slot, long total, long revision, long utcTicks)
    {
        if (installation == Guid.Empty || run == Guid.Empty || slot < 1 || slot > 4 || total < 0 || revision < 1 || utcTicks < 621355968000000000L || utcTicks > DateTime.MaxValue.Ticks)
            throw new ArgumentException("Invalid persistent death record.", nameof(total));
        Installation = installation; Run = run; Slot = slot; Total = total; Revision = revision; UtcTicks = utcTicks;
    }
    public Guid Installation { get; }
    public Guid Run { get; }
    public int Slot { get; }
    public long Total { get; }
    public long Revision { get; }
    public long UtcTicks { get; }

    public string Encode()
    {
        string body = string.Join("\n", "SOULSTRACKER-HK-1", Installation.ToString("D"), Run.ToString("D"),
            Slot.ToString(CultureInfo.InvariantCulture), Total.ToString(CultureInfo.InvariantCulture),
            Revision.ToString(CultureInfo.InvariantCulture), UtcTicks.ToString(CultureInfo.InvariantCulture)) + "\n";
        using (SHA256 hash = SHA256.Create())
            return body + BitConverter.ToString(hash.ComputeHash(Encoding.UTF8.GetBytes(body))).Replace("-", "") + "\n";
    }

    public static DeathRecord? Decode(string? text)
    {
        if (text == null || text.Length > 2048) return null;
        string[] lines = text.Split('\n');
        if (lines.Length != 9 || lines[0] != "SOULSTRACKER-HK-1" || lines[8] != "" ||
            !Guid.TryParseExact(lines[1], "D", out Guid install) || !Guid.TryParseExact(lines[2], "D", out Guid run) ||
            !int.TryParse(lines[3], NumberStyles.None, CultureInfo.InvariantCulture, out int slot) ||
            !long.TryParse(lines[4], NumberStyles.None, CultureInfo.InvariantCulture, out long total) ||
            !long.TryParse(lines[5], NumberStyles.None, CultureInfo.InvariantCulture, out long revision) ||
            !long.TryParse(lines[6], NumberStyles.None, CultureInfo.InvariantCulture, out long ticks)) return null;
        try
        {
            var record = new DeathRecord(install, run, slot, total, revision, ticks);
            return record.Encode() == text ? record : null;
        }
        catch (ArgumentException) { return null; }
    }
}
