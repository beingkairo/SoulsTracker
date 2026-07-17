namespace SoulsTracker.Infrastructure;

/// <summary>
/// Validates only the observed Dark Souls Remastered candidate identity. A
/// matching candidate is not a reader binding or a supported-profile claim.
/// </summary>
public sealed class DarkSoulsRemasteredCandidateIdentityValidator
{
    private const string ExpectedExecutableFileName = "DarkSoulsRemastered.exe";
    private const string ExpectedFileVersion = "1,0,0,0";
    private const string ExpectedProductVersion = "1";
    private const string ExpectedSha256 = "A45AAA36DD2F6CC151670A639EA5547043CF38EA79FF4178B963C6ED71F98D7B";
    private readonly IReadOnlyProcessAttachmentFactory attachmentFactory;

    public DarkSoulsRemasteredCandidateIdentityValidator(IReadOnlyProcessAttachmentFactory attachmentFactory)
    {
        this.attachmentFactory = attachmentFactory ?? throw new ArgumentNullException(nameof(attachmentFactory));
    }

    /// <summary>
    /// Validates the supplied process identity and always detaches before it
    /// returns. It never reads virtual memory.
    /// </summary>
    public async ValueTask<DarkSoulsRemasteredCandidateIdentityValidationResult> ValidateAsync(
        int processId,
        CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested)
        {
            return DarkSoulsRemasteredCandidateIdentityValidationResult.Cancelled();
        }

        ReadOnlyProcessAttachmentResult attachmentResult;
        try
        {
            attachmentResult = await attachmentFactory.AttachAsync(processId, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return DarkSoulsRemasteredCandidateIdentityValidationResult.Cancelled();
        }
        catch (IOException)
        {
            return DarkSoulsRemasteredCandidateIdentityValidationResult.Unsupported();
        }
        catch (UnauthorizedAccessException)
        {
            return DarkSoulsRemasteredCandidateIdentityValidationResult.Unsupported();
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return DarkSoulsRemasteredCandidateIdentityValidationResult.Unsupported();
        }
        catch (InvalidOperationException)
        {
            return DarkSoulsRemasteredCandidateIdentityValidationResult.Unsupported();
        }
        catch (System.Security.SecurityException)
        {
            return DarkSoulsRemasteredCandidateIdentityValidationResult.Unsupported();
        }

        if (attachmentResult.Outcome == ReadOnlyProcessAttachmentOutcome.Cancelled)
        {
            return DarkSoulsRemasteredCandidateIdentityValidationResult.Cancelled();
        }

        if (attachmentResult.Outcome != ReadOnlyProcessAttachmentOutcome.Attached || attachmentResult.Attachment is null)
        {
            return DarkSoulsRemasteredCandidateIdentityValidationResult.Unsupported();
        }

        await using IReadOnlyProcessAttachment attachment = attachmentResult.Attachment;
        return await ValidateAttachedAsync(attachment, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Validates an existing attachment without detaching it. This permits a
    /// bounded test harness to verify identity on the same attachment before
    /// each authorized read while the normal validator remains read-free.
    /// </summary>
    internal static async ValueTask<DarkSoulsRemasteredCandidateIdentityValidationResult> ValidateAttachedAsync(
        IReadOnlyProcessAttachment attachment,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(attachment);
        if (cancellationToken.IsCancellationRequested)
        {
            return DarkSoulsRemasteredCandidateIdentityValidationResult.Cancelled();
        }

        try
        {
            ReadOnlyModuleIdentityResult identityResult = await attachment.QueryMainModuleIdentityAsync(cancellationToken).ConfigureAwait(false);
            if (identityResult.Outcome == ReadOnlyModuleIdentityOutcome.Cancelled)
            {
                return DarkSoulsRemasteredCandidateIdentityValidationResult.Cancelled();
            }

            if (identityResult.Outcome != ReadOnlyModuleIdentityOutcome.Available || identityResult.Identity is null)
            {
                return DarkSoulsRemasteredCandidateIdentityValidationResult.Unsupported();
            }

            return IsExactCandidate(identityResult.Identity)
                ? DarkSoulsRemasteredCandidateIdentityValidationResult.MatchedCandidate()
                : DarkSoulsRemasteredCandidateIdentityValidationResult.Unsupported();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return DarkSoulsRemasteredCandidateIdentityValidationResult.Cancelled();
        }
        catch (IOException)
        {
            return DarkSoulsRemasteredCandidateIdentityValidationResult.Unsupported();
        }
        catch (UnauthorizedAccessException)
        {
            return DarkSoulsRemasteredCandidateIdentityValidationResult.Unsupported();
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return DarkSoulsRemasteredCandidateIdentityValidationResult.Unsupported();
        }
        catch (InvalidOperationException)
        {
            return DarkSoulsRemasteredCandidateIdentityValidationResult.Unsupported();
        }
        catch (System.Security.SecurityException)
        {
            return DarkSoulsRemasteredCandidateIdentityValidationResult.Unsupported();
        }
    }

    private static bool IsExactCandidate(ProcessModuleFileIdentity identity) =>
        string.Equals(identity.ExecutableFileName, ExpectedExecutableFileName, StringComparison.OrdinalIgnoreCase) &&
        string.Equals(identity.FileVersion, ExpectedFileVersion, StringComparison.Ordinal) &&
        string.Equals(identity.ProductVersion, ExpectedProductVersion, StringComparison.Ordinal) &&
        string.Equals(identity.Sha256, ExpectedSha256, StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// Deliberately contains no diagnostic details. A match identifies only the
/// observed candidate for a future live-validation ticket.
/// </summary>
public sealed class DarkSoulsRemasteredCandidateIdentityValidationResult
{
    private DarkSoulsRemasteredCandidateIdentityValidationResult(DarkSoulsRemasteredCandidateIdentityValidationOutcome outcome)
    {
        Outcome = outcome;
    }

    public DarkSoulsRemasteredCandidateIdentityValidationOutcome Outcome { get; }

    internal static DarkSoulsRemasteredCandidateIdentityValidationResult MatchedCandidate() =>
        new(DarkSoulsRemasteredCandidateIdentityValidationOutcome.MatchedCandidate);

    internal static DarkSoulsRemasteredCandidateIdentityValidationResult Unsupported() =>
        new(DarkSoulsRemasteredCandidateIdentityValidationOutcome.Unsupported);

    internal static DarkSoulsRemasteredCandidateIdentityValidationResult Cancelled() =>
        new(DarkSoulsRemasteredCandidateIdentityValidationOutcome.Cancelled);
}

/// <summary>
/// A candidate match is not a product support or reader-capability declaration.
/// </summary>
public enum DarkSoulsRemasteredCandidateIdentityValidationOutcome
{
    MatchedCandidate,
    Unsupported,
    Cancelled,
}
