using System.ComponentModel;
using System.Diagnostics;
using SoulsTracker.Domain;

namespace SoulsTracker.Infrastructure;

/// <summary>Owns the selected game's read-only attachment and pending observed event.</summary>
public sealed class HollowKnightRuntimeReader : IAsyncDisposable
{
    private readonly HollowKnightDeathDetector detector;
    private readonly Func<IReadOnlyList<HollowKnightProcessIdentity>> enumerate;
    private readonly TimeProvider clock;
    private readonly IReadOnlyProcessAttachmentFactory attachments;
    private IReadOnlyProcessAttachment? memory;
    private HollowKnightProcessIdentity? process;
    private nuint root;
    private DateTimeOffset nextProcessCheck;
    private DateTimeOffset nextDiscovery;

    public HollowKnightRuntimeReader() : this(Enumerate, new WindowsReadOnlyProcessAttachmentFactory()) { }

    internal HollowKnightRuntimeReader(Func<IReadOnlyList<HollowKnightProcessIdentity>> enumerate,
        IReadOnlyProcessAttachmentFactory attachments, TimeProvider? timeProvider = null)
    {
        this.enumerate = enumerate;
        this.attachments = attachments;
        clock = timeProvider ?? TimeProvider.System;
        detector = new(clock);
    }

    private static IReadOnlyList<HollowKnightProcessIdentity> Enumerate()
    {
        Process[] candidates = [.. Process.GetProcessesByName("hollow_knight"), .. Process.GetProcessesByName("Hollow Knight")];
        try
        {
            return candidates.Where(p => HollowKnightMemory.IsOriginalProcessName(p.ProcessName))
                .Select(p => new HollowKnightProcessIdentity(p.Id, p.StartTime)).ToArray();
        }
        finally { foreach (Process candidate in candidates) candidate.Dispose(); }
    }

    public Guid? PendingEvent => detector.PendingEvent;
    public void Acknowledge(Guid eventId) => detector.Acknowledge(eventId);

    public async ValueTask ResetAsync()
    {
        detector.ResetContinuity();
        root = 0;
        if (memory is not null) await memory.DisposeAsync().ConfigureAwait(false);
        memory = null;
        process = null;
        nextProcessCheck = default;
        nextDiscovery = default;
    }

    public async ValueTask<RuntimeGameReadResult> ReadAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            DateTimeOffset now = clock.GetUtcNow();
            if (now >= nextProcessCheck)
            {
                IReadOnlyList<HollowKnightProcessIdentity> candidates = enumerate();
                if (candidates.Count != 1)
                {
                    await ResetAsync().ConfigureAwait(false);
                    nextProcessCheck = now.AddSeconds(1);
                    return RuntimeGameReadResult.Unavailable(GameId.HollowKnight);
                }
                if (process != candidates[0] || memory is null)
                {
                    await ResetAsync().ConfigureAwait(false);
                    process = candidates[0];
                    ReadOnlyProcessAttachmentResult attached = await attachments.AttachAsync(candidates[0].ProcessId, cancellationToken).ConfigureAwait(false);
                    memory = attached.Attachment;
                }
                nextProcessCheck = now.AddSeconds(1);
            }
            if (memory is null) return RuntimeGameReadResult.Unavailable(GameId.HollowKnight);
            if (root == 0 && now >= nextDiscovery)
            {
                nextDiscovery = now.AddSeconds(10);
                root = await HollowKnightMemory.FindRootAsync(memory, cancellationToken).ConfigureAwait(false);
            }
            HollowKnightSample? sample = root == 0 ? null : await HollowKnightMemory.SampleAsync(memory, root, cancellationToken).ConfigureAwait(false);
            detector.Observe(sample);
            if (sample is null)
            {
                return RuntimeGameReadResult.Unavailable(GameId.HollowKnight);
            }
            return sample.Value.GameState == 4 ? RuntimeGameReadResult.MonitoringHollowKnight()
                : RuntimeGameReadResult.WaitingForActiveCharacter(GameId.HollowKnight);
        }
        catch (Exception exception) when (exception is Win32Exception or InvalidOperationException or ArgumentException or OverflowException)
        {
            await ResetAsync().ConfigureAwait(false);
            return RuntimeGameReadResult.Unavailable(GameId.HollowKnight);
        }
    }

    public ValueTask DisposeAsync() => ResetAsync();
}

internal readonly record struct HollowKnightProcessIdentity(int ProcessId, DateTime StartedAt);
