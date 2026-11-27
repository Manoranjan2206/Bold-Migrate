using BoldMigrate.Core.Enums;
using BoldMigrate.Core.Models;
using BoldMigrate.Core.StateMachine;
using Xunit;

namespace BoldMigrate.Core.Tests;

public class ReportStateMachineTests
{
    [Theory]
    [InlineData(ReportState.Discovered, ReportState.Staged)]
    [InlineData(ReportState.Staged, ReportState.Analysed)]
    [InlineData(ReportState.Analysed, ReportState.Blocked)]
    [InlineData(ReportState.Analysed, ReportState.Queued)]
    [InlineData(ReportState.Analysed, ReportState.Validating)]
    [InlineData(ReportState.Analysed, ReportState.Excluded)]
    [InlineData(ReportState.Queued, ReportState.Validating)]
    [InlineData(ReportState.Queued, ReportState.Uploading)]
    [InlineData(ReportState.Uploading, ReportState.Uploaded)]
    [InlineData(ReportState.Uploading, ReportState.UploadFailed)]
    [InlineData(ReportState.UploadFailed, ReportState.Queued)]
    [InlineData(ReportState.Uploaded, ReportState.Validating)]
    [InlineData(ReportState.Validating, ReportState.AutoPassed)]
    [InlineData(ReportState.Validating, ReportState.AwaitingReview)]
    [InlineData(ReportState.Validating, ReportState.ValidationFailed)]
    [InlineData(ReportState.ValidationFailed, ReportState.Validating)]
    [InlineData(ReportState.ValidationFailed, ReportState.AwaitingReview)]
    [InlineData(ReportState.AwaitingReview, ReportState.Approved)]
    [InlineData(ReportState.AwaitingReview, ReportState.Flagged)]
    [InlineData(ReportState.Flagged, ReportState.Validating)]
    [InlineData(ReportState.Flagged, ReportState.AwaitingReview)]
    [InlineData(ReportState.AutoPassed, ReportState.Approved)]
    [InlineData(ReportState.AutoPassed, ReportState.Queued)]
    [InlineData(ReportState.AutoPassed, ReportState.Uploading)]
    [InlineData(ReportState.Approved, ReportState.Queued)]
    [InlineData(ReportState.Approved, ReportState.Uploading)]
    public void CanTransition_LegalTransitions_ReturnTrue(ReportState from, ReportState to)
    {
        Assert.True(ReportStateMachine.CanTransition(from, to));
    }

    [Theory]
    [InlineData(ReportState.Discovered, ReportState.Uploaded)]
    [InlineData(ReportState.Discovered, ReportState.AutoPassed)]
    [InlineData(ReportState.Uploading, ReportState.Approved)]
    [InlineData(ReportState.ValidationFailed, ReportState.Uploading)]
    [InlineData(ReportState.Approved, ReportState.Discovered)]
    public void CanTransition_IllegalTransitions_ReturnFalse(ReportState from, ReportState to)
    {
        Assert.False(ReportStateMachine.CanTransition(from, to));
    }

    [Fact]
    public void AnyState_ExceptExcluded_CanTransitionToExcluded()
    {
        var states = Enum.GetValues<ReportState>().Where(s => s != ReportState.Excluded);
        foreach (var state in states)
        {
            Assert.True(ReportStateMachine.CanTransition(state, ReportState.Excluded));
        }

        Assert.False(ReportStateMachine.CanTransition(ReportState.Excluded, ReportState.Excluded));
    }

    [Fact]
    public void Transition_IllegalTransition_ThrowsInvalidOperationException()
    {
        var report = new ReportRecord
        {
            CatalogItemId = Guid.NewGuid(),
            State = ReportState.Discovered
        };

        Assert.Throws<InvalidOperationException>(() =>
            ReportStateMachine.Transition(report, ReportState.AutoPassed));
    }

    [Fact]
    public void Transition_LegalTransition_UpdatesStateAndExcludedReason()
    {
        var report = new ReportRecord
        {
            CatalogItemId = Guid.NewGuid(),
            State = ReportState.Analysed
        };

        ReportStateMachine.Transition(report, ReportState.Excluded, "Excluded by user decision");

        Assert.Equal(ReportState.Excluded, report.State);
        Assert.Equal("Excluded by user decision", report.ExcludedReason);
    }

    /// <summary>
    /// Regression: de-selecting a report in Step 2 and re-selecting it used to come back as
    /// Analysed, silently throwing away an approval and forcing the operator to validate again.
    /// </summary>
    [Theory]
    [InlineData(ReportState.Approved)]
    [InlineData(ReportState.AutoPassed)]
    [InlineData(ReportState.AwaitingReview)]
    [InlineData(ReportState.Uploaded)]
    public void DeselectThenReselect_RestoresThePreviousState(ReportState original)
    {
        var report = new ReportRecord { CatalogItemId = Guid.NewGuid(), State = original };

        ReportStateMachine.ExcludeForDeselect(report);
        Assert.Equal(ReportState.Excluded, report.State);

        var restored = ReportStateMachine.RestoreFromDeselect(report);

        Assert.Equal(original, restored);
        Assert.Equal(original, report.State);
        Assert.Null(report.ExcludedReason);
    }

    [Fact]
    public void RestoreFromDeselect_WithoutMarker_FallsBackToAnalysed()
    {
        var report = new ReportRecord { CatalogItemId = Guid.NewGuid(), State = ReportState.Analysed };
        ReportStateMachine.Transition(report, ReportState.Excluded, "Blocked by compatibility rule");

        var restored = ReportStateMachine.RestoreFromDeselect(report);

        Assert.Equal(ReportState.Analysed, restored);
    }

    [Fact]
    public void TryTransition_ReturnsFalseInsteadOfThrowingOnIllegalMove()
    {
        var report = new ReportRecord { CatalogItemId = Guid.NewGuid(), State = ReportState.Discovered };

        Assert.False(ReportStateMachine.TryTransition(report, ReportState.Uploaded));
        Assert.Equal(ReportState.Discovered, report.State);

        Assert.True(ReportStateMachine.TryTransition(report, ReportState.Analysed));
        Assert.Equal(ReportState.Analysed, report.State);
    }

    [Fact]
    public void TryTransition_IsIdempotentForTheCurrentState()
    {
        var report = new ReportRecord { CatalogItemId = Guid.NewGuid(), State = ReportState.Approved };

        Assert.True(ReportStateMachine.TryTransition(report, ReportState.Approved));
        Assert.Equal(ReportState.Approved, report.State);
    }
}
