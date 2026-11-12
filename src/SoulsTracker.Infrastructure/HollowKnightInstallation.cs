using System.Diagnostics;
using System.Reflection;
using System.Reflection.Emit;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;
using System.Text.RegularExpressions;
using Microsoft.Win32;
using Soulstracker.DeathTracking;

namespace SoulsTracker.Infrastructure;

public sealed record HollowKnightInstallation(string Directory, string Version, bool HasApi, bool Supported);

/// <summary>Bounded Steam discovery and metadata inspection; no game assembly is loaded or executed.</summary>
public static partial class HollowKnightInstallDiscovery
{
    public const string SupportedGameVersion = "1.5.78.11833";
    internal static bool IsGameProcessName(string name) =>
        string.Equals(name, "hollow_knight", StringComparison.OrdinalIgnoreCase) || string.Equals(name, "Hollow Knight", StringComparison.OrdinalIgnoreCase);
    public static bool IsGameRunning()
    {
        Process[] processes = [.. Process.GetProcessesByName("hollow_knight"), .. Process.GetProcessesByName("Hollow Knight")];
        try { return processes.Length != 0; }
        finally { foreach (Process process in processes) process.Dispose(); }
    }

    public static string? Discover()
    {
        if (!OperatingSystem.IsWindows()) return null;
        using RegistryKey? key = Registry.CurrentUser.OpenSubKey(@"Software\Valve\Steam");
        string? steam = key?.GetValue("SteamPath") as string;
        return DiscoverFromSteamRoots([steam ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Steam")]);
    }

    internal static string? DiscoverFromSteamRoots(IEnumerable<string> supplied)
    {
        var roots = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        static string Canonical(string path) => Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        foreach (string steam in supplied.Take(16))
        {
            try { roots.Add(Canonical(steam)); } catch (Exception exception) when (exception is ArgumentException or IOException or UnauthorizedAccessException or NotSupportedException) { }
            try
            {
                string file = Path.Combine(steam, "steamapps", "libraryfolders.vdf");
                if (new FileInfo(file) is { Exists: true, Length: <= 262144 })
                    foreach (Match match in LibraryPath().Matches(File.ReadAllText(file)).Cast<Match>().Take(64))
                        roots.Add(Canonical(match.Groups[1].Value.Replace(@"\\", @"\", StringComparison.Ordinal)));
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
        string[] candidates = roots.Select(root => Path.Combine(root, "steamapps", "common", "Hollow Knight"))
            .Where(path => Inspect(path) is not null).Select(Canonical).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        return candidates.Length == 1 ? candidates[0] : null;
    }

    [GeneratedRegex("\"path\"\\s*\"([^\"]+)\"", RegexOptions.IgnoreCase)]
    private static partial Regex LibraryPath();

    public static HollowKnightInstallation? Inspect(string path)
    {
        try
        {
            path = Path.GetFullPath(path);
            EnsureSafePath(path);
            if (!File.Exists(Path.Combine(path, "hollow_knight.exe"))) return null;
            string assembly = Path.Combine(path, "hollow_knight_Data", "Managed", "Assembly-CSharp.dll");
            EnsureSafePath(assembly);
            if (new FileInfo(assembly).Length > 16777216) return null;
            using FileStream stream = File.OpenRead(assembly);
            using var pe = new PEReader(stream);
            MetadataReader reader = pe.GetMetadataReader();
            bool api = reader.TypeDefinitions.Any(handle => reader.GetString(reader.GetTypeDefinition(handle).Namespace) == "Modding");
            string? version = null;
            foreach (TypeDefinitionHandle handle in reader.TypeDefinitions)
            {
                TypeDefinition type = reader.GetTypeDefinition(handle);
                if (reader.GetString(type.Name) != "OnScreenDebugInfo") continue;
                foreach (MethodDefinitionHandle methodHandle in type.GetMethods())
                {
                    MethodDefinition method = reader.GetMethodDefinition(methodHandle);
                    if (reader.GetString(method.Name) != "RetrieveInfo" || method.RelativeVirtualAddress == 0) continue;
                    version = ReadStrings(pe.GetMethodBody(method.RelativeVirtualAddress).GetILBytes()!, reader)
                        .SingleOrDefault(text => GameVersion().IsMatch(text));
                }
            }
            if (version is null) return null;
            return new(path, version, api, HollowKnightCompatibility.SupportsGame(version));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or BadImageFormatException or ArgumentException or InvalidOperationException or IndexOutOfRangeException or KeyNotFoundException or OverflowException)
        { return null; }
    }

    private static readonly Dictionary<ushort, OpCode> Codes = typeof(OpCodes).GetFields(BindingFlags.Public | BindingFlags.Static)
        .Where(field => field.FieldType == typeof(OpCode)).Select(field => (OpCode)field.GetValue(null)!).ToDictionary(code => (ushort)code.Value);

    private static IEnumerable<string> ReadStrings(byte[] il, MetadataReader reader)
    {
        for (int cursor = 0; cursor < il.Length;)
        {
            ushort value = il[cursor++];
            if (value == 0xfe) value = (ushort)(0xfe00 | il[cursor++]);
            OpCode code = Codes[value];
            if (code.OperandType == OperandType.InlineString) yield return reader.GetUserString(MetadataTokens.UserStringHandle(BitConverter.ToInt32(il, cursor) & 0xffffff));
            cursor += code.OperandType switch
            {
                OperandType.InlineNone => 0,
                OperandType.ShortInlineBrTarget or OperandType.ShortInlineI or OperandType.ShortInlineVar => 1,
                OperandType.InlineVar => 2,
                OperandType.InlineI8 or OperandType.InlineR => 8,
                OperandType.InlineSwitch => 4 + checked(4 * BitConverter.ToInt32(il, cursor)),
                _ => 4
            };
        }
    }

    [GeneratedRegex(@"^1\.\d+\.\d+(?:\.\d+)?$", RegexOptions.CultureInvariant)]
    private static partial Regex GameVersion();

    internal static void EnsureSafePath(string path)
    {
        if (!Path.IsPathFullyQualified(path) || path.StartsWith(@"\\", StringComparison.Ordinal)) throw new IOException("Choose a local game directory.");
        string? cursor = path;
        while (cursor is not null)
        {
            if ((File.Exists(cursor) || System.IO.Directory.Exists(cursor)) && (File.GetAttributes(cursor) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("Linked installation paths are unsupported.");
            cursor = Path.GetDirectoryName(cursor);
        }
    }
}
