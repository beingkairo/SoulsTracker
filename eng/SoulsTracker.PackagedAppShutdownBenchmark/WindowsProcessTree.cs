using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace SoulsTracker.PackagedAppShutdownBenchmark;

internal sealed class WindowsProcessTree : IDisposable
{
    private const uint SnapshotProcesses = 0x00000002;
    private readonly Dictionary<int, Process> ownedProcesses = [];
    private bool disposed;

    public WindowsProcessTree(Process rootProcess)
    {
        ArgumentNullException.ThrowIfNull(rootProcess);
        ownedProcesses.Add(rootProcess.Id, rootProcess);
        Refresh();
    }

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
        Dictionary<int, int> parents = SnapshotParentProcessIds();
        IReadOnlySet<int> ownedIds = ProcessTreeOwnership.ExpandOwnedProcessIds(
            ownedProcesses.Keys,
            parents);
        foreach (int processId in ownedIds)
        {
            if (ownedProcesses.ContainsKey(processId))
            {
                continue;
            }

            try
            {
                ownedProcesses.Add(processId, Process.GetProcessById(processId));
            }
            catch (ArgumentException)
            {
                // The child exited between the process snapshot and opening its handle.
            }
        }
    }

    public void KillRemainingAfterMeasurement()
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        Refresh();
        foreach (Process process in ownedProcesses.Values.Where(process => !IsExited(process)))
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
        foreach (Process process in ownedProcesses.Values)
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
        foreach (Process process in ownedProcesses.Values)
        {
            process.Dispose();
        }

        ownedProcesses.Clear();
    }

    private static bool IsExited(Process process)
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

    private static Dictionary<int, int> SnapshotParentProcessIds()
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

internal static class ProcessTreeOwnership
{
    public static IReadOnlySet<int> ExpandOwnedProcessIds(
        IEnumerable<int> roots,
        IReadOnlyDictionary<int, int> parentProcessIds)
    {
        var owned = roots.ToHashSet();
        bool added;
        do
        {
            added = false;
            foreach ((int processId, int parentProcessId) in parentProcessIds)
            {
                if (!owned.Contains(processId) && owned.Contains(parentProcessId))
                {
                    owned.Add(processId);
                    added = true;
                }
            }
        }
        while (added);

        return owned;
    }
}
