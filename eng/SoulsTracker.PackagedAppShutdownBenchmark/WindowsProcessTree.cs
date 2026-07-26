using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace SoulsTracker.PackagedAppShutdownBenchmark;

internal sealed class WindowsProcessTree : IDisposable
{
    private readonly IProcessSnapshotSource snapshotSource;
    private readonly IProcessHandleFactory processFactory;
    private readonly Dictionary<int, IOwnedProcessHandle> ownedProcesses = [];
    private bool disposed;

    public WindowsProcessTree(Process rootProcess)
        : this(
            new SystemProcessHandle(rootProcess ?? throw new ArgumentNullException(nameof(rootProcess))),
            new ToolhelpProcessSnapshotSource(),
            new SystemProcessHandleFactory())
    {
    }

    internal WindowsProcessTree(
        IOwnedProcessHandle rootProcess,
        IProcessSnapshotSource snapshotSource,
        IProcessHandleFactory processFactory)
    {
        ArgumentNullException.ThrowIfNull(rootProcess);
        this.snapshotSource = snapshotSource ?? throw new ArgumentNullException(nameof(snapshotSource));
        this.processFactory = processFactory ?? throw new ArgumentNullException(nameof(processFactory));
        ownedProcesses.Add(rootProcess.Id, rootProcess);
        Refresh();
    }

    internal IReadOnlySet<int> OwnedProcessIds => ownedProcesses.Keys.ToHashSet();

    public bool AllExited
    {
        get
        {
            Refresh();
            return ownedProcesses.Values.All(IsExited);
        }
    }

    public void Refresh()
    {
        ObjectDisposedException.ThrowIf(disposed, this);

        bool retainedCandidate;
        do
        {
            retainedCandidate = false;
            IReadOnlyDictionary<int, int> initialSnapshot = snapshotSource.CaptureParentProcessIds();
            IOwnedProcessHandle[] liveParents = ownedProcesses.Values
                .Where(process => !IsExited(process))
                .ToArray();
            foreach (IOwnedProcessHandle parent in liveParents)
            {
                int[] candidateIds = initialSnapshot
                    .Where(entry => entry.Value == parent.Id && !ownedProcesses.ContainsKey(entry.Key))
                    .Select(entry => entry.Key)
                    .ToArray();
                foreach (int candidateId in candidateIds)
                {
                    IOwnedProcessHandle? candidate = TryOpen(candidateId);
                    if (candidate is null)
                    {
                        continue;
                    }

                    if (CanRetainCandidate(candidateId, candidate, parent))
                    {
                        ownedProcesses.Add(candidateId, candidate);
                        retainedCandidate = true;
                    }
                    else
                    {
                        candidate.Dispose();
                    }
                }
            }
        }
        while (retainedCandidate);
    }

    public void KillRemainingAfterMeasurement()
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        Refresh();
        foreach (IOwnedProcessHandle process in ownedProcesses.Values.Where(process => !IsExited(process)))
        {
            try
            {
                process.Kill();
            }
            catch (InvalidOperationException)
            {
            }
            catch (SystemException)
            {
            }
        }
    }

    public async Task WaitForExitAfterCleanupAsync(TimeSpan timeout)
    {
        using var cancellation = new CancellationTokenSource(timeout);
        foreach (IOwnedProcessHandle process in ownedProcesses.Values)
        {
            try
            {
                await process.WaitForExitAsync(cancellation.Token).ConfigureAwait(false);
            }
            catch (InvalidOperationException)
            {
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    public void Dispose()
    {
        if (disposed)
        {
            return;
        }

        disposed = true;
        foreach (IOwnedProcessHandle process in ownedProcesses.Values)
        {
            process.Dispose();
        }

        ownedProcesses.Clear();
    }

    private bool CanRetainCandidate(
        int expectedCandidateId,
        IOwnedProcessHandle candidate,
        IOwnedProcessHandle parent)
    {
        if (candidate.Id != expectedCandidateId || IsExited(candidate) || IsExited(parent))
        {
            return false;
        }

        IReadOnlyDictionary<int, int> verificationSnapshot = snapshotSource.CaptureParentProcessIds();
        return verificationSnapshot.TryGetValue(expectedCandidateId, out int verifiedParentId) &&
            verifiedParentId == parent.Id &&
            !IsExited(candidate) &&
            !IsExited(parent);
    }

    private IOwnedProcessHandle? TryOpen(int processId)
    {
        try
        {
            return processFactory.Open(processId);
        }
        catch (ArgumentException)
        {
            return null;
        }
        catch (InvalidOperationException)
        {
            return null;
        }
    }

    private static bool IsExited(IOwnedProcessHandle process)
    {
        try
        {
            return process.HasExited;
        }
        catch (InvalidOperationException)
        {
            return true;
        }
    }
}

internal interface IOwnedProcessHandle : IDisposable
{
    int Id { get; }
    bool HasExited { get; }
    void Kill();
    Task WaitForExitAsync(CancellationToken cancellationToken);
}

internal interface IProcessHandleFactory
{
    IOwnedProcessHandle Open(int processId);
}

internal interface IProcessSnapshotSource
{
    IReadOnlyDictionary<int, int> CaptureParentProcessIds();
}

internal sealed class SystemProcessHandle(Process process) : IOwnedProcessHandle
{
    private readonly Process process = process ?? throw new ArgumentNullException(nameof(process));

    public int Id => process.Id;
    public bool HasExited => process.HasExited;
    public void Kill() => process.Kill();
    public Task WaitForExitAsync(CancellationToken cancellationToken) =>
        process.WaitForExitAsync(cancellationToken);
    public void Dispose() => process.Dispose();
}

internal sealed class SystemProcessHandleFactory : IProcessHandleFactory
{
    public IOwnedProcessHandle Open(int processId) =>
        new SystemProcessHandle(Process.GetProcessById(processId));
}

internal sealed class ToolhelpProcessSnapshotSource : IProcessSnapshotSource
{
    private const uint SnapshotProcesses = 0x00000002;

    public IReadOnlyDictionary<int, int> CaptureParentProcessIds()
    {
        nint snapshot = CreateToolhelp32Snapshot(SnapshotProcesses, 0);
        if (snapshot == -1)
        {
            throw new Win32Exception(Marshal.GetLastWin32Error());
        }

        try
        {
            var entry = new ProcessEntry32
            {
                Size = (uint)Marshal.SizeOf<ProcessEntry32>(),
            };
            var result = new Dictionary<int, int>();
            if (!Process32First(snapshot, ref entry))
            {
                int error = Marshal.GetLastWin32Error();
                if (error == 18)
                {
                    return result;
                }

                throw new Win32Exception(error);
            }

            do
            {
                result[(int)entry.ProcessId] = (int)entry.ParentProcessId;
                entry.Size = (uint)Marshal.SizeOf<ProcessEntry32>();
            }
            while (Process32Next(snapshot, ref entry));

            return result;
        }
        finally
        {
            CloseHandle(snapshot);
        }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ProcessEntry32
    {
        public uint Size;
        public uint Usage;
        public uint ProcessId;
        public nint DefaultHeapId;
        public uint ModuleId;
        public uint Threads;
        public uint ParentProcessId;
        public int PriorityClassBase;
        public uint Flags;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
        public string ExecutableFile;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern nint CreateToolhelp32Snapshot(uint flags, uint processId);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool Process32First(nint snapshot, ref ProcessEntry32 entry);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool Process32Next(nint snapshot, ref ProcessEntry32 entry);

    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(nint handle);
}
