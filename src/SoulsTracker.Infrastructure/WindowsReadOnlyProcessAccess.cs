using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using System.Security.Cryptography;

namespace SoulsTracker.Infrastructure;

/// <summary>
/// Creates one Windows process attachment that is restricted to query and
/// virtual-memory read access. Callers must dispose every returned attachment.
/// </summary>
public interface IReadOnlyProcessAttachmentFactory
{
    /// <summary>
    /// Attempts to attach to the supplied process without granting write or
    /// execution capabilities.
    /// </summary>
    ValueTask<ReadOnlyProcessAttachmentResult> AttachAsync(int processId, CancellationToken cancellationToken);
}

/// <summary>
/// Represents one bounded read/query process attachment. This is an inert
/// primitive; it does not identify a supported game or bind a tracker reader.
/// </summary>
public interface IReadOnlyProcessAttachment : IAsyncDisposable
{
    /// <summary>
    /// Retrieves only the main executable's identity evidence.
    /// </summary>
    ValueTask<ReadOnlyModuleIdentityResult> QueryMainModuleIdentityAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Retrieves the already-attached process's main-module base for a
    /// version-locked, bounded test reader. The base is never exposed by a
    /// public result property.
    /// </summary>
    ValueTask<ReadOnlyMainModuleBaseResult> QueryMainModuleBaseAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Reads a caller-provided buffer from an already-attached process. No
    /// caller in this ticket is authorized to invoke this future-reader primitive.
    /// </summary>
    ValueTask<ReadOnlyMemoryReadResult> ReadVirtualMemoryAsync(
        nuint address,
        byte[] destination,
        CancellationToken cancellationToken);
}

/// <summary>
/// Contains the safe outcome of an attachment attempt.
/// </summary>
public sealed class ReadOnlyProcessAttachmentResult
{
    private ReadOnlyProcessAttachmentResult(ReadOnlyProcessAttachmentOutcome outcome, IReadOnlyProcessAttachment? attachment)
    {
        Outcome = outcome;
        Attachment = attachment;
    }

    public ReadOnlyProcessAttachmentOutcome Outcome { get; }

    public IReadOnlyProcessAttachment? Attachment { get; }

    public static ReadOnlyProcessAttachmentResult Attached(IReadOnlyProcessAttachment attachment) =>
        new(ReadOnlyProcessAttachmentOutcome.Attached, attachment ?? throw new ArgumentNullException(nameof(attachment)));

    public static ReadOnlyProcessAttachmentResult Unavailable() => new(ReadOnlyProcessAttachmentOutcome.Unavailable, null);

    public static ReadOnlyProcessAttachmentResult Cancelled() => new(ReadOnlyProcessAttachmentOutcome.Cancelled, null);
}

/// <summary>
/// Does not disclose process identifiers, handles, paths, or native errors.
/// </summary>
public enum ReadOnlyProcessAttachmentOutcome
{
    Attached,
    Unavailable,
    Cancelled,
}

/// <summary>
/// Contains one main-module identity query result.
/// </summary>
public sealed class ReadOnlyModuleIdentityResult
{
    private ReadOnlyModuleIdentityResult(ReadOnlyModuleIdentityOutcome outcome, ProcessModuleFileIdentity? identity)
    {
        Outcome = outcome;
        Identity = identity;
    }

    public ReadOnlyModuleIdentityOutcome Outcome { get; }

    public ProcessModuleFileIdentity? Identity { get; }

    public static ReadOnlyModuleIdentityResult Available(ProcessModuleFileIdentity identity) =>
        new(ReadOnlyModuleIdentityOutcome.Available, identity ?? throw new ArgumentNullException(nameof(identity)));

    public static ReadOnlyModuleIdentityResult Unavailable() => new(ReadOnlyModuleIdentityOutcome.Unavailable, null);

    public static ReadOnlyModuleIdentityResult Cancelled() => new(ReadOnlyModuleIdentityOutcome.Cancelled, null);
}

/// <summary>
/// Does not expose a raw module path or process identifier. This evidence is
/// intended solely for an in-process version-locked validator.
/// </summary>
public sealed record ProcessModuleFileIdentity(
    string ExecutableFileName,
    string FileVersion,
    string ProductVersion,
    string Sha256,
    string ProductName = "");

/// <summary>
/// Uses a safe fixed result for unavailable, inaccessible, or cancelled identity queries.
/// </summary>
public enum ReadOnlyModuleIdentityOutcome
{
    Available,
    Unavailable,
    Cancelled,
}

/// <summary>
/// Contains the safe outcome of a bounded main-module-base query. The base is
/// intentionally internal so callers cannot serialize or display it.
/// </summary>
public sealed class ReadOnlyMainModuleBaseResult
{
    private ReadOnlyMainModuleBaseResult(ReadOnlyMainModuleBaseOutcome outcome, nuint baseAddress)
    {
        Outcome = outcome;
        BaseAddress = baseAddress;
    }

    public ReadOnlyMainModuleBaseOutcome Outcome { get; }

    internal nuint BaseAddress { get; }

    internal static ReadOnlyMainModuleBaseResult Available(nuint baseAddress) =>
        new(ReadOnlyMainModuleBaseOutcome.Available, baseAddress);

    public static ReadOnlyMainModuleBaseResult Unavailable() => new(ReadOnlyMainModuleBaseOutcome.Unavailable, 0);

    public static ReadOnlyMainModuleBaseResult Cancelled() => new(ReadOnlyMainModuleBaseOutcome.Cancelled, 0);
}

/// <summary>
/// Does not disclose a module base, path, process identifier, or native error.
/// </summary>
public enum ReadOnlyMainModuleBaseOutcome
{
    Available,
    Unavailable,
    Cancelled,
}

/// <summary>
/// Contains only byte-count metadata; read bytes remain in the caller-supplied buffer.
/// </summary>
public sealed class ReadOnlyMemoryReadResult
{
    private ReadOnlyMemoryReadResult(ReadOnlyMemoryReadOutcome outcome, nuint bytesRead)
    {
        Outcome = outcome;
        BytesRead = bytesRead;
    }

    public ReadOnlyMemoryReadOutcome Outcome { get; }

    public nuint BytesRead { get; }

    public static ReadOnlyMemoryReadResult Succeeded(nuint bytesRead) => new(ReadOnlyMemoryReadOutcome.Succeeded, bytesRead);

    public static ReadOnlyMemoryReadResult Unavailable() => new(ReadOnlyMemoryReadOutcome.Unavailable, 0);

    public static ReadOnlyMemoryReadResult Cancelled() => new(ReadOnlyMemoryReadOutcome.Cancelled, 0);
}

/// <summary>
/// Describes a virtual-memory read without exposing its contents in diagnostics.
/// </summary>
public enum ReadOnlyMemoryReadOutcome
{
    Succeeded,
    Unavailable,
    Cancelled,
}

/// <summary>
/// Windows-only implementation of the neutral read/query attachment primitive.
/// It is intentionally not composed into the application in this ticket.
/// </summary>
internal static class ReadOnlyMainModuleSnapshot
{
    internal const int MaximumModuleHandleCount = 4_096;

    /// <summary>
    /// The PSAPI module-information contract documents the first entry as the
    /// executable. Accept it only from a complete, bounded snapshot.
    /// </summary>
    internal static bool TrySelectDocumentedMainModule(
        nint[] modules,
        uint returnedByteCount,
        out nint mainModule)
    {
        mainModule = 0;
        if (modules is null || modules.Length is 0 or > MaximumModuleHandleCount)
        {
            return false;
        }

        uint expectedByteCount;
        try
        {
            expectedByteCount = checked((uint)(modules.Length * IntPtr.Size));
        }
        catch (OverflowException)
        {
            return false;
        }

        if (returnedByteCount != expectedByteCount || modules[0] == 0)
        {
            return false;
        }

        mainModule = modules[0];
        return true;
    }
}

public sealed class WindowsReadOnlyProcessAttachmentFactory : IReadOnlyProcessAttachmentFactory
{
    private const uint ProcessVmRead = 0x0010;
    private const uint ProcessQueryInformation = 0x0400;

    /// <inheritdoc />
    public ValueTask<ReadOnlyProcessAttachmentResult> AttachAsync(int processId, CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested)
        {
            return ValueTask.FromResult(ReadOnlyProcessAttachmentResult.Cancelled());
        }

        if (processId <= 0 || !OperatingSystem.IsWindows())
        {
            return ValueTask.FromResult(ReadOnlyProcessAttachmentResult.Unavailable());
        }

        try
        {
            SafeProcessHandle handle = OpenProcess(ProcessVmRead | ProcessQueryInformation, false, (uint)processId);
            if (handle.IsInvalid)
            {
                handle.Dispose();
                return ValueTask.FromResult(ReadOnlyProcessAttachmentResult.Unavailable());
            }

            if (cancellationToken.IsCancellationRequested)
            {
                handle.Dispose();
                return ValueTask.FromResult(ReadOnlyProcessAttachmentResult.Cancelled());
            }

            return ValueTask.FromResult(ReadOnlyProcessAttachmentResult.Attached(new WindowsReadOnlyProcessAttachment(handle)));
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return ValueTask.FromResult(ReadOnlyProcessAttachmentResult.Unavailable());
        }
        catch (UnauthorizedAccessException)
        {
            return ValueTask.FromResult(ReadOnlyProcessAttachmentResult.Unavailable());
        }
        catch (InvalidOperationException)
        {
            return ValueTask.FromResult(ReadOnlyProcessAttachmentResult.Unavailable());
        }
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern SafeProcessHandle OpenProcess(uint desiredAccess, [MarshalAs(UnmanagedType.Bool)] bool inheritHandle, uint processId);

    private sealed class SafeProcessHandle : SafeHandleZeroOrMinusOneIsInvalid
    {
        public SafeProcessHandle()
            : base(ownsHandle: true)
        {
        }

        protected override bool ReleaseHandle() => CloseHandle(handle);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool CloseHandle(nint handle);
    }

    private sealed class WindowsReadOnlyProcessAttachment : IReadOnlyProcessAttachment
    {
        private readonly SafeProcessHandle handle;
        private bool disposed;

        public WindowsReadOnlyProcessAttachment(SafeProcessHandle handle)
        {
            this.handle = handle;
        }

        public ValueTask<ReadOnlyModuleIdentityResult> QueryMainModuleIdentityAsync(CancellationToken cancellationToken)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                return ValueTask.FromResult(ReadOnlyModuleIdentityResult.Cancelled());
            }

            if (disposed || handle.IsInvalid || handle.IsClosed)
            {
                return ValueTask.FromResult(ReadOnlyModuleIdentityResult.Unavailable());
            }

            try
            {
                string? imagePath = TryGetImagePath();
                if (string.IsNullOrEmpty(imagePath) || cancellationToken.IsCancellationRequested)
                {
                    return ValueTask.FromResult(cancellationToken.IsCancellationRequested
                        ? ReadOnlyModuleIdentityResult.Cancelled()
                        : ReadOnlyModuleIdentityResult.Unavailable());
                }

                FileVersionInfo versionInfo = FileVersionInfo.GetVersionInfo(imagePath);
                string fileName = Path.GetFileName(imagePath);
                string fileVersion = versionInfo.FileVersion ?? string.Empty;
                string productVersion = versionInfo.ProductVersion ?? string.Empty;
                string sha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(imagePath)));
                if (cancellationToken.IsCancellationRequested)
                {
                    return ValueTask.FromResult(ReadOnlyModuleIdentityResult.Cancelled());
                }

                return ValueTask.FromResult(ReadOnlyModuleIdentityResult.Available(
                    new ProcessModuleFileIdentity(fileName, fileVersion, productVersion, sha256, versionInfo.ProductName ?? string.Empty)));
            }
            catch (System.ComponentModel.Win32Exception)
            {
                return ValueTask.FromResult(ReadOnlyModuleIdentityResult.Unavailable());
            }
            catch (IOException)
            {
                return ValueTask.FromResult(ReadOnlyModuleIdentityResult.Unavailable());
            }
            catch (UnauthorizedAccessException)
            {
                return ValueTask.FromResult(ReadOnlyModuleIdentityResult.Unavailable());
            }
            catch (ArgumentException)
            {
                return ValueTask.FromResult(ReadOnlyModuleIdentityResult.Unavailable());
            }
            catch (NotSupportedException)
            {
                return ValueTask.FromResult(ReadOnlyModuleIdentityResult.Unavailable());
            }
            catch (System.Security.SecurityException)
            {
                return ValueTask.FromResult(ReadOnlyModuleIdentityResult.Unavailable());
            }
            catch (CryptographicException)
            {
                return ValueTask.FromResult(ReadOnlyModuleIdentityResult.Unavailable());
            }
        }

        public ValueTask<ReadOnlyMemoryReadResult> ReadVirtualMemoryAsync(
            nuint address,
            byte[] destination,
            CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(destination);
            if (cancellationToken.IsCancellationRequested)
            {
                return ValueTask.FromResult(ReadOnlyMemoryReadResult.Cancelled());
            }

            if (disposed || handle.IsInvalid || handle.IsClosed)
            {
                return ValueTask.FromResult(ReadOnlyMemoryReadResult.Unavailable());
            }

            if (destination.Length == 0)
            {
                return ValueTask.FromResult(ReadOnlyMemoryReadResult.Succeeded(0));
            }

            try
            {
                bool read = ReadProcessMemory(handle, address, destination, (nuint)destination.Length, out nuint bytesRead);
                if (cancellationToken.IsCancellationRequested)
                {
                    return ValueTask.FromResult(ReadOnlyMemoryReadResult.Cancelled());
                }

                return ValueTask.FromResult(read
                    ? ReadOnlyMemoryReadResult.Succeeded(bytesRead)
                    : ReadOnlyMemoryReadResult.Unavailable());
            }
            catch (System.ComponentModel.Win32Exception)
            {
                return ValueTask.FromResult(ReadOnlyMemoryReadResult.Unavailable());
            }
            catch (UnauthorizedAccessException)
            {
                return ValueTask.FromResult(ReadOnlyMemoryReadResult.Unavailable());
            }
            catch (InvalidOperationException)
            {
                return ValueTask.FromResult(ReadOnlyMemoryReadResult.Unavailable());
            }
        }

        public ValueTask<ReadOnlyMainModuleBaseResult> QueryMainModuleBaseAsync(CancellationToken cancellationToken)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                return ValueTask.FromResult(ReadOnlyMainModuleBaseResult.Cancelled());
            }

            if (disposed || handle.IsInvalid || handle.IsClosed)
            {
                return ValueTask.FromResult(ReadOnlyMainModuleBaseResult.Unavailable());
            }

            try
            {
                nint[] probe = new nint[1];
                uint probeSize = checked((uint)(probe.Length * IntPtr.Size));
                bool probed = K32EnumProcessModules(handle, probe, probeSize, out uint requiredSize);
                if (cancellationToken.IsCancellationRequested)
                {
                    return ValueTask.FromResult(ReadOnlyMainModuleBaseResult.Cancelled());
                }

                if (!probed || requiredSize == 0 || requiredSize % (uint)IntPtr.Size != 0)
                {
                    return ValueTask.FromResult(ReadOnlyMainModuleBaseResult.Unavailable());
                }

                int moduleCount = checked((int)(requiredSize / (uint)IntPtr.Size));
                if (moduleCount > ReadOnlyMainModuleSnapshot.MaximumModuleHandleCount)
                {
                    return ValueTask.FromResult(ReadOnlyMainModuleBaseResult.Unavailable());
                }

                var modules = new nint[moduleCount];
                uint snapshotSize = checked((uint)(modules.Length * IntPtr.Size));
                bool enumerated = K32EnumProcessModules(handle, modules, snapshotSize, out uint snapshotByteCount);
                if (cancellationToken.IsCancellationRequested)
                {
                    return ValueTask.FromResult(ReadOnlyMainModuleBaseResult.Cancelled());
                }

                if (!enumerated || !ReadOnlyMainModuleSnapshot.TrySelectDocumentedMainModule(modules, snapshotByteCount, out nint mainModule))
                {
                    return ValueTask.FromResult(ReadOnlyMainModuleBaseResult.Unavailable());
                }

                bool queried = K32GetModuleInformation(handle, mainModule, out ModuleInformation information, (uint)Marshal.SizeOf<ModuleInformation>());
                if (cancellationToken.IsCancellationRequested)
                {
                    return ValueTask.FromResult(ReadOnlyMainModuleBaseResult.Cancelled());
                }

                return ValueTask.FromResult(queried && information.BaseAddress != 0
                    ? ReadOnlyMainModuleBaseResult.Available((nuint)information.BaseAddress)
                    : ReadOnlyMainModuleBaseResult.Unavailable());
            }
            catch (System.ComponentModel.Win32Exception)
            {
                return ValueTask.FromResult(ReadOnlyMainModuleBaseResult.Unavailable());
            }
            catch (UnauthorizedAccessException)
            {
                return ValueTask.FromResult(ReadOnlyMainModuleBaseResult.Unavailable());
            }
            catch (InvalidOperationException)
            {
                return ValueTask.FromResult(ReadOnlyMainModuleBaseResult.Unavailable());
            }
            catch (OverflowException)
            {
                return ValueTask.FromResult(ReadOnlyMainModuleBaseResult.Unavailable());
            }
        }

        public ValueTask DisposeAsync()
        {
            if (!disposed)
            {
                disposed = true;
                handle.Dispose();
            }

            return ValueTask.CompletedTask;
        }

        private string? TryGetImagePath()
        {
            const int InitialBufferLength = 32_768;
            var buffer = new char[InitialBufferLength];
            uint size = (uint)buffer.Length;
            return QueryFullProcessImageName(handle, 0, buffer, ref size)
                ? new string(buffer, 0, checked((int)size))
                : null;
        }

        [DllImport("kernel32.dll", EntryPoint = "QueryFullProcessImageNameW", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool QueryFullProcessImageName(
            SafeProcessHandle process,
            uint flags,
            [Out] char[] executableName,
            ref uint size);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool ReadProcessMemory(
            SafeProcessHandle process,
            nuint baseAddress,
            [Out] byte[] buffer,
            nuint size,
            out nuint bytesRead);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool K32EnumProcessModules(
            SafeProcessHandle process,
            [Out] nint[] modules,
            uint bufferSize,
            out uint requiredSize);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool K32GetModuleInformation(
            SafeProcessHandle process,
            nint module,
            out ModuleInformation information,
            uint informationSize);

        [StructLayout(LayoutKind.Sequential)]
        private struct ModuleInformation
        {
            public nint BaseAddress;
            public uint ImageSize;
            public nint EntryPoint;
        }
    }
}
