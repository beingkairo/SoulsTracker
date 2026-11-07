using SoulsTracker.Infrastructure;
using System.Diagnostics;

if (!OperatingSystem.IsWindows() || !Environment.Is64BitProcess || args.Length is < 1 or > 2 ||
    args.Length == 2 && args[1] is not ("--once" or "--inspect") ||
    !Enum.TryParse(args[0], true, out BloodborneCompatibilityRuntime runtime) || !Enum.IsDefined(runtime))
{
    Console.Error.WriteLine("Usage (64-bit Windows): dotnet run --project eng/SoulsTracker.BloodborneCompatibilityProbe -- ShadPs4|Bbport|LanternEngine [--once|--inspect]");
    return 2;
}

Console.WriteLine($"Read-only validation: {runtime}. These samples are not published to SoulsTracker. Ctrl+C stops.");
if (args.Length == 2 && args[1] == "--inspect" && runtime == BloodborneCompatibilityRuntime.ShadPs4)
{
    foreach (Process process in Process.GetProcessesByName("shadPS4"))
    {
        using (process)
        {
            Console.WriteLine($"Process: {process.ProcessName} PID={process.Id} title={process.MainWindowTitle}");
            try { Console.WriteLine($"Executable: {process.MainModule?.FileName ?? "unavailable"}"); }
            catch (System.ComponentModel.Win32Exception) { Console.WriteLine("Executable path requires matching process privileges."); }
            catch (InvalidOperationException) { Console.WriteLine("Process exited during inspection."); }
        }
    }
    RuntimeGameReadResult? original = await new BloodborneActiveCharacterDeathReader(
        new ExactNameBloodborneProcessEnumerator(), new WindowsReadOnlyProcessAttachmentFactory()).ReadAsync(default);
    Console.WriteLine($"Existing v2.1.1 reader: {original?.Status.ToString() ?? "Unavailable (no observation)"} total={original?.Observation?.TotalDeaths.Value.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "?"}");
}
using var stopping = new CancellationTokenSource();
Console.CancelKeyPress += (_, eventArgs) => { eventArgs.Cancel = true; stopping.Cancel(); };
BloodborneCompatibilityProbe probe = BloodborneCompatibilityProbe.Create(runtime);
try
{
    while (!stopping.IsCancellationRequested)
    {
        BloodborneCompatibilitySample sample = await probe.SampleAsync(stopping.Token);
        string relation = sample.Status == BloodborneCompatibilityStatus.Sampled ? sample.LegacyRelation.ToString() : "NotChecked";
        Console.WriteLine($"{DateTimeOffset.Now:HH:mm:ss} {sample.Status} deaths={sample.Deaths?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "?"} true_deaths={sample.TrueDeaths?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "?"} legacy={sample.LegacyValue?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "?"} legacy_read={sample.LegacyRead} legacy_address={relation}");
        if (sample.Status == BloodborneCompatibilityStatus.AttachmentDenied)
            Console.WriteLine("Windows denied read/query access (error 5). If the game is elevated, run this diagnostic from an Administrator PowerShell; leave the running game unchanged.");
        if (args.Length == 2) break;
        await Task.Delay(TimeSpan.FromSeconds(2), stopping.Token);
    }
}
catch (OperationCanceledException) when (stopping.IsCancellationRequested) { }
return 0;
