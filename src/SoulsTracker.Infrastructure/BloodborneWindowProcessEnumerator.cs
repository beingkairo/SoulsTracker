using System.Runtime.InteropServices;

namespace SoulsTracker.Infrastructure;

internal sealed record BloodborneWindow(nint Handle, int ProcessId, string Title, bool Visible = true, bool Owned = false);

internal interface IBloodborneWindowSource
{
    IReadOnlyList<BloodborneWindow> Enumerate(CancellationToken cancellationToken);
    BloodborneWindow? Read(nint handle);
}

/// <summary>Discovers game-window owners without enumerating Windows processes or relying on executable names.</summary>
public sealed class BloodborneWindowProcessEnumerator : IBloodborneProcessEnumerator
{
    internal const int MaximumWindows = 2048;
    internal const int MaximumCandidates = 8;
    internal const int MaximumTitleCharacters = 512;
    private readonly IBloodborneWindowSource source;

    public BloodborneWindowProcessEnumerator() : this(new WindowsSource()) { }
    internal BloodborneWindowProcessEnumerator(IBloodborneWindowSource source) => this.source = source;

    public ValueTask<IReadOnlyList<IBloodborneProcessCandidate>> EnumerateExactCandidatesAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        IReadOnlyList<BloodborneWindow> windows = source.Enumerate(cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        if (windows.Count > MaximumWindows) throw new IOException("Bloodborne window-discovery budget exceeded.");
        var candidates = new Dictionary<int, BloodborneWindow>();
        foreach (BloodborneWindow window in windows)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!IsEligible(window)) continue;
            candidates.TryAdd(window.ProcessId, window);
            if (candidates.Count > MaximumCandidates) throw new IOException("Bloodborne candidate budget exceeded.");
        }
        return ValueTask.FromResult<IReadOnlyList<IBloodborneProcessCandidate>>(
            candidates.Values.Select(window => (IBloodborneProcessCandidate)new Candidate(source, window)).ToArray());
    }

    internal static bool IsEligible(BloodborneWindow window)
    {
        if (!window.Visible || window.Owned || window.ProcessId <= 0 || window.ProcessId == Environment.ProcessId ||
            window.Title.Length >= MaximumTitleCharacters) return false;
        const string name = "Bloodborne";
        ReadOnlySpan<char> title = window.Title.AsSpan().TrimStart();
        return title.StartsWith(name, StringComparison.OrdinalIgnoreCase) &&
            (title.Length == name.Length || !char.IsLetterOrDigit(title[name.Length]));
    }

    private sealed class Candidate(IBloodborneWindowSource source, BloodborneWindow window) : IBloodborneProcessCandidate
    {
        public int ProcessId => window.ProcessId;
        public ValueTask<bool> IsExpectedGuestActiveAsync(CancellationToken cancellationToken)
        {
            BloodborneWindow? current = cancellationToken.IsCancellationRequested ? null : source.Read(window.Handle);
            return ValueTask.FromResult(current is not null && current.ProcessId == window.ProcessId && IsEligible(current));
        }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class WindowsSource : IBloodborneWindowSource
    {
        public IReadOnlyList<BloodborneWindow> Enumerate(CancellationToken cancellationToken)
        {
            if (!OperatingSystem.IsWindows()) return [];
            var found = new List<BloodborneWindow>();
            int visited = 0;
            bool complete = EnumWindows((handle, _) =>
            {
                if (cancellationToken.IsCancellationRequested || ++visited > MaximumWindows) return false;
                BloodborneWindow? window = Read(handle);
                if (window is not null && IsEligible(window)) found.Add(window);
                return true;
            }, 0);
            cancellationToken.ThrowIfCancellationRequested();
            if (!complete) throw new IOException("Bloodborne window enumeration was incomplete.");
            return found;
        }

        public BloodborneWindow? Read(nint handle)
        {
            if (!OperatingSystem.IsWindows() || !IsWindowVisible(handle) || GetWindow(handle, 4) != 0) return null;
            if (GetWindowThreadProcessId(handle, out uint processId) == 0 || processId == 0 ||
                processId > int.MaxValue || processId == Environment.ProcessId) return null;
            // Skip our own windows before GetWindowText: no synchronous self-window messaging.
            char[] title = new char[MaximumTitleCharacters];
            int length = GetWindowText(handle, title, title.Length);
            return length is > 0 and < MaximumTitleCharacters - 1
                ? new(handle, (int)processId, new string(title, 0, length)) : null;
        }

        [return: MarshalAs(UnmanagedType.Bool)]
        private delegate bool WindowCallback(nint handle, nint parameter);
        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool EnumWindows(WindowCallback callback, nint parameter);
        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool IsWindowVisible(nint handle);
        [DllImport("user32.dll")]
        private static extern nint GetWindow(nint handle, uint command);
        [DllImport("user32.dll", EntryPoint = "GetWindowTextW", CharSet = CharSet.Unicode)]
        private static extern int GetWindowText(nint handle, [Out] char[] text, int maximumCount);
        [DllImport("user32.dll")]
        private static extern uint GetWindowThreadProcessId(nint handle, out uint processId);
    }
}
