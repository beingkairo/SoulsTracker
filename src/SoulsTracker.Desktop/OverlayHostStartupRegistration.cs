using Microsoft.Win32;
using System.IO;

namespace SoulsTracker.Desktop;

/// <summary>Current-user Run-key registration for the explicit persistent overlay option.</summary>
public interface IOverlayHostStartupRegistration
{
    bool IsEnabled { get; }
    void Enable();
    void Disable();
}

internal interface IOverlayHostRunKey
{
    string? Read(string name);
    void Write(string name, string commandLine);
    void Remove(string name);
}

public sealed class OverlayHostStartupRegistration : IOverlayHostStartupRegistration
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "SoulsTracker Overlay Host";
    private readonly string hostExecutable;
    private readonly IOverlayHostRunKey runKey;

    public OverlayHostStartupRegistration(string hostExecutable)
        : this(hostExecutable, new CurrentUserRunKey())
    {
    }

    internal OverlayHostStartupRegistration(string hostExecutable, IOverlayHostRunKey runKey)
    {
        if (string.IsNullOrWhiteSpace(hostExecutable) || !Path.IsPathFullyQualified(hostExecutable)) throw new ArgumentException("The overlay host path must be absolute.", nameof(hostExecutable));
        this.hostExecutable = hostExecutable;
        this.runKey = runKey ?? throw new ArgumentNullException(nameof(runKey));
    }

    public bool IsEnabled
    {
        get
        {
            return string.Equals(runKey.Read(ValueName), CommandLine, StringComparison.Ordinal);
        }
    }

    public void Enable()
    {
        runKey.Write(ValueName, CommandLine);
    }

    public void Disable()
    {
        runKey.Remove(ValueName);
    }

    private string CommandLine => $"\"{hostExecutable}\"";

    private sealed class CurrentUserRunKey : IOverlayHostRunKey
    {
        public string? Read(string name)
        {
            using RegistryKey? key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: false);
            return key?.GetValue(name) as string;
        }

        public void Write(string name, string commandLine)
        {
            using RegistryKey key = Registry.CurrentUser.CreateSubKey(RunKeyPath, writable: true);
            key.SetValue(name, commandLine, RegistryValueKind.String);
        }

        public void Remove(string name)
        {
            using RegistryKey? key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true);
            key?.DeleteValue(name, throwOnMissingValue: false);
        }
    }
}
