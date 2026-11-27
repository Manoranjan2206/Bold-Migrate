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
        // Discovery stages and classifies in one pass, so a record goes straight from Discovered
        // to Analysed. The table previously forced a Staged hop that no code performed, which is
        // why StartCheckAsync assigned State directly instead of calling Transition.
        (ReportState.Discovered, ReportState.Analysed),
        // Re-running the compatibility check re-analyses an already-analysed report.
        (ReportState.Analysed, ReportState.Analysed),
        // Re-selecting a report that was de-selected in Step 2 returns it to the pool. Without
        // these the controller had to bypass the state machine to make selection work at all.
        // The validated states are included so re-selecting an already-approved report restores
        // its approval instead of silently demoting it to Analysed and forcing re-validation.
        (ReportState.Excluded, ReportState.Analysed),
        (ReportState.Excluded, ReportState.Queued),
        (ReportState.Excluded, ReportState.AutoPassed),
        (ReportState.Excluded, ReportState.Approved),
        (ReportState.Excluded, ReportState.AwaitingReview),
        (ReportState.Excluded, ReportState.ValidationFailed),
        (ReportState.Excluded, ReportState.Flagged),
        (ReportState.Excluded, ReportState.Uploaded),
        (ReportState.Excluded, ReportState.UploadFailed),
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
        (ReportState.Uploaded, ReportState.Uploading),
        // Re-assessing an estate, or deleting items from the destination, resets validated and
        // uploaded reports back to the analysed pool. These paths existed already but assigned
        // State directly because the table did not describe them.
        (ReportState.AutoPassed, ReportState.Analysed),
        (ReportState.AwaitingReview, ReportState.Analysed),
        (ReportState.ValidationFailed, ReportState.Analysed),
        (ReportState.Flagged, ReportState.Analysed),
        (ReportState.Approved, ReportState.Analysed),
        (ReportState.Queued, ReportState.Analysed),
        (ReportState.Uploaded, ReportState.Analysed),
        (ReportState.UploadFailed, ReportState.Analysed),
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

    /// <summary>
    /// Applies a transition if it is legal, returning false instead of throwing when it is not.
    /// Intended for request handlers where an illegal transition should be logged and skipped
    /// rather than turned into a 500 — the point is that callers stop assigning State directly.
    /// </summary>
    public static bool TryTransition(ReportRecord report, ReportState to, string? excludedReason = null)
    {
        ArgumentNullException.ThrowIfNull(report);

        if (report.State == to) return true;
        if (!CanTransition(report.State, to)) return false;

        Transition(report, to, excludedReason);
        return true;
    }

    /// <summary>Marker stored in ExcludedReason so an exclusion's prior state can be restored.</summary>
    private const string DeselectPrefix = "deselected-from:";

    /// <summary>
    /// Excludes a report that the operator de-selected in Step 2, remembering the state it was in.
    /// </summary>
    /// <remarks>
    /// De-selecting used to discard the report's progress: re-selecting it came back as Analysed,
    /// so an already-approved report silently needed validating again. The prior state is stashed
    /// in the existing ExcludedReason column, which avoids a schema change.
    /// </remarks>
    public static void ExcludeForDeselect(ReportRecord report)
    {
        ArgumentNullException.ThrowIfNull(report);
        if (report.State == ReportState.Excluded) return;

        var previous = report.State;
        Transition(report, ReportState.Excluded, $"{DeselectPrefix}{previous}");
    }

    /// <summary>
    /// Re-includes a de-selected report, restoring the state it held before exclusion when that is
    /// still a legal destination. Falls back to <see cref="ReportState.Analysed"/> otherwise.
    /// </summary>
    public static ReportState RestoreFromDeselect(ReportRecord report)
    {
        ArgumentNullException.ThrowIfNull(report);
        if (report.State != ReportState.Excluded) return report.State;

        var target = ReportState.Analysed;
        var reason = report.ExcludedReason;

        if (!string.IsNullOrWhiteSpace(reason) && reason.StartsWith(DeselectPrefix, StringComparison.Ordinal))
        {
            var raw = reason[DeselectPrefix.Length..];
            if (Enum.TryParse<ReportState>(raw, out var previous) &&
                previous != ReportState.Excluded &&
                CanTransition(ReportState.Excluded, previous))
            {
                target = previous;
            }
        }

        Transition(report, target);
        report.ExcludedReason = null;
        return target;
    }
}
