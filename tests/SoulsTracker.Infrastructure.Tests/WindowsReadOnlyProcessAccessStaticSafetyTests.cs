using System.Reflection;
using SoulsTracker.Infrastructure;

namespace SoulsTracker.Infrastructure.Tests;

public sealed class WindowsReadOnlyProcessAccessStaticSafetyTests
{
    [Fact]
    public void NativeSurfaceUsesOnlyApprovedReadQueryAndDetachDeclarations()
    {
        string source = File.ReadAllText(GetSourcePath());

        Assert.Equal(6, Count(source, "[DllImport("));
        Assert.Contains("OpenProcess", source, StringComparison.Ordinal);
        Assert.Contains("QueryFullProcessImageName", source, StringComparison.Ordinal);
        Assert.Contains("ReadProcessMemory", source, StringComparison.Ordinal);
        Assert.Contains("K32EnumProcessModules", source, StringComparison.Ordinal);
        Assert.Contains("K32GetModuleInformation", source, StringComparison.Ordinal);
        Assert.Contains("var modules = new nint[moduleCount];", source, StringComparison.Ordinal);
        Assert.Contains("TrySelectDocumentedMainModule", source, StringComparison.Ordinal);
        Assert.Contains("CloseHandle", source, StringComparison.Ordinal);
        Assert.Contains("private const uint ProcessVmRead = 0x0010;", source, StringComparison.Ordinal);
        Assert.Contains("private const uint ProcessQueryInformation = 0x0400;", source, StringComparison.Ordinal);
        Assert.DoesNotContain("ProcessQueryLimitedInformation", source, StringComparison.Ordinal);
        Assert.DoesNotContain("0x0020", source, StringComparison.Ordinal);
        Assert.DoesNotContain("0x0002", source, StringComparison.Ordinal);
        Assert.DoesNotContain("VirtualQueryEx", source, StringComparison.Ordinal);
    }

    [Fact]
    public void CompleteMultiModuleSnapshotSelectsOnlyTheDocumentedExecutableEntry()
    {
        nint[] modules = [(nint)1, (nint)2, (nint)3];
        uint byteCount = checked((uint)(modules.Length * IntPtr.Size));

        bool selected = ReadOnlyMainModuleSnapshot.TrySelectDocumentedMainModule(modules, byteCount, out nint mainModule);

        Assert.True(selected);
        Assert.Equal(modules[0], mainModule);
    }

    [Fact]
    public void IncompleteAmbiguousOrOversizedModuleSnapshotsFailClosed()
    {
        nint[] modules = [(nint)1, (nint)2];
        uint completeByteCount = checked((uint)(modules.Length * IntPtr.Size));

        Assert.False(ReadOnlyMainModuleSnapshot.TrySelectDocumentedMainModule(modules, completeByteCount - (uint)IntPtr.Size, out _));
        Assert.False(ReadOnlyMainModuleSnapshot.TrySelectDocumentedMainModule([(nint)0], (uint)IntPtr.Size, out _));
        Assert.False(ReadOnlyMainModuleSnapshot.TrySelectDocumentedMainModule(
            new nint[ReadOnlyMainModuleSnapshot.MaximumModuleHandleCount + 1],
            checked((uint)((ReadOnlyMainModuleSnapshot.MaximumModuleHandleCount + 1) * IntPtr.Size)),
            out _));
    }

    [Theory]
    [InlineData("WriteProcessMemory")]
    [InlineData("VirtualAllocEx")]
    [InlineData("VirtualProtectEx")]
    [InlineData("VirtualFreeEx")]
    [InlineData("CreateRemoteThread")]
    [InlineData("QueueUserAPC")]
    [InlineData("SetWindowsHookEx")]
    [InlineData("SendInput")]
    [InlineData("keybd_event")]
    [InlineData("mouse_event")]
    [InlineData("TerminateProcess")]
    [InlineData("SuspendThread")]
    [InlineData("ResumeThread")]
    [InlineData("DebugActiveProcess")]
    [InlineData("Process.Start")]
    [InlineData("File.Write")]
    [InlineData("File.Delete")]
    [InlineData("File.Move")]
    public void NativeSurfaceContainsNoWriteInjectionInputOrBroadProcessControlApi(string forbiddenApi)
    {
        string source = File.ReadAllText(GetSourcePath());

        Assert.DoesNotContain(forbiddenApi, source, StringComparison.Ordinal);
    }

    [Fact]
    public void NewProcessFoundationDoesNotSwallowBroadExceptions()
    {
        string[] sourceFiles =
        [
            GetSourcePath(),
            GetInfrastructureSourcePath("DarkSoulsRemasteredCandidateIdentityValidator.cs"),
        ];

        foreach (string sourceFile in sourceFiles)
        {
            string source = File.ReadAllText(sourceFile);
            Assert.DoesNotContain("catch (Exception", source, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void PublicOutcomesContainNoRawHandlePathProcessOrExceptionDiagnostic()
    {
        Type[] resultTypes =
        [
            typeof(ReadOnlyProcessAttachmentResult),
            typeof(ReadOnlyModuleIdentityResult),
            typeof(ReadOnlyMemoryReadResult),
            typeof(ReadOnlyMainModuleBaseResult),
            typeof(DarkSoulsRemasteredCandidateIdentityValidationResult),
        ];

        foreach (Type resultType in resultTypes)
        {
            foreach (PropertyInfo property in resultType.GetProperties(BindingFlags.Instance | BindingFlags.Public))
            {
                Assert.False(property.Name.Contains("Handle", StringComparison.Ordinal));
                Assert.False(property.Name.Contains("Path", StringComparison.Ordinal));
                Assert.False(property.Name.Contains("BaseAddress", StringComparison.Ordinal));
                Assert.False(property.Name.Contains("ProcessId", StringComparison.Ordinal));
                Assert.False(property.PropertyType == typeof(Exception));
            }
        }
    }

    private static int Count(string source, string value)
    {
        int count = 0;
        int index = 0;
        while ((index = source.IndexOf(value, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += value.Length;
        }

        return count;
    }

    private static string GetSourcePath() => GetInfrastructureSourcePath("WindowsReadOnlyProcessAccess.cs");

    private static string GetInfrastructureSourcePath(string fileName) => Path.GetFullPath(Path.Combine(
        AppContext.BaseDirectory,
        "..",
        "..",
        "..",
        "..",
        "..",
        "src",
        "SoulsTracker.Infrastructure",
        fileName));
}
