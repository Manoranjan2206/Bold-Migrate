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
}
