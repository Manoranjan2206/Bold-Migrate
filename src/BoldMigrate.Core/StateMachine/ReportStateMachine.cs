using BoldMigrate.Core.Enums;
using BoldMigrate.Core.Models;

namespace BoldMigrate.Core.StateMachine;

/// <summary>
/// Enforces the legal transitions defined in the Bold Migrate specification state machine.
/// </summary>
public static class ReportStateMachine
{
    private static readonly HashSet<(ReportState From, ReportState To)> LegalTransitions = new()
    {
        (ReportState.Discovered, ReportState.Staged),
        (ReportState.Staged, ReportState.Analysed),
        (ReportState.Analysed, ReportState.Blocked),
        (ReportState.Analysed, ReportState.Queued),
        (ReportState.Analysed, ReportState.Validating),
        (ReportState.Analysed, ReportState.Excluded),
        (ReportState.Queued, ReportState.Validating),
        (ReportState.Queued, ReportState.Uploading),
        (ReportState.Validating, ReportState.AutoPassed),
        (ReportState.Validating, ReportState.AwaitingReview),
        (ReportState.Validating, ReportState.ValidationFailed),
        (ReportState.ValidationFailed, ReportState.Validating),
        (ReportState.ValidationFailed, ReportState.AwaitingReview),
        (ReportState.AwaitingReview, ReportState.Approved),
        (ReportState.AwaitingReview, ReportState.Flagged),
        (ReportState.AwaitingReview, ReportState.Validating),
        (ReportState.Flagged, ReportState.Validating),
        (ReportState.Flagged, ReportState.AwaitingReview),
        (ReportState.AutoPassed, ReportState.Approved),
        (ReportState.AutoPassed, ReportState.Queued),
        (ReportState.AutoPassed, ReportState.Uploading),
        (ReportState.AutoPassed, ReportState.Validating),
        (ReportState.Approved, ReportState.Queued),
        (ReportState.Approved, ReportState.Uploading),
        (ReportState.Approved, ReportState.Validating),
        (ReportState.Uploading, ReportState.Uploaded),
        (ReportState.Uploading, ReportState.UploadFailed),
        (ReportState.UploadFailed, ReportState.Queued),
        (ReportState.UploadFailed, ReportState.Uploading),
        (ReportState.Uploaded, ReportState.Validating),
    };

    /// <summary>
    /// Checks whether transitioning from <paramref name="from"/> to <paramref name="to"/> is permitted.
    /// Any state except Excluded can transition to Excluded.
    /// </summary>
    public static bool CanTransition(ReportState from, ReportState to)
    {
        if (to == ReportState.Excluded)
        {
            return from != ReportState.Excluded;
        }

        return LegalTransitions.Contains((from, to));
    }

    /// <summary>
    /// Applies a state transition to the specified report record, or throws an InvalidOperationException if illegal.
    /// </summary>
    public static ReportState Transition(ReportRecord report, ReportState to, string? excludedReason = null)
    {
        ArgumentNullException.ThrowIfNull(report);

        if (!CanTransition(report.State, to))
        {
            throw new InvalidOperationException(
                $"Illegal state transition for report '{report.CatalogItemId}': cannot move from '{report.State}' to '{to}'.");
        }

        if (to == ReportState.Excluded && !string.IsNullOrWhiteSpace(excludedReason))
        {
            report.ExcludedReason = excludedReason;
        }

        report.State = to;
        return to;
    }
}
