using System.Diagnostics.Metrics;

namespace BoldMigrate.Engine.Metrics;

public static class MigrationMetrics
{
    private static readonly Meter Meter = new("BoldMigrate", "1.0.0");

    public static readonly Counter<long> RunsTotal = Meter.CreateCounter<long>(
        "boldmigrate_runs_total",
        description: "How many migrations start and how many finish");

    public static readonly Histogram<double> RunDurationSeconds = Meter.CreateHistogram<double>(
        "boldmigrate_run_duration_seconds",
        unit: "s",
        description: "Duration of migration run steps");

    public static readonly Counter<long> ItemsTotal = Meter.CreateCounter<long>(
        "boldmigrate_items_total",
        description: "Throughput and failure rate per stage");

    public static readonly Histogram<double> ItemDurationSeconds = Meter.CreateHistogram<double>(
        "boldmigrate_item_duration_seconds",
        unit: "s",
        description: "Per-item cost for capacity planning");

    public static readonly Counter<long> SourceRequestsTotal = Meter.CreateCounter<long>(
        "boldmigrate_source_requests_total",
        description: "Pressure on source server");

    public static readonly Counter<long> TransformAttemptsTotal = Meter.CreateCounter<long>(
        "boldmigrate_transform_attempts_total",
        description: "Transform attempts");

    public static readonly Counter<long> ClassificationTotal = Meter.CreateCounter<long>(
        "boldmigrate_classification_total",
        description: "Classification counts by bucket");

    public static readonly Histogram<double> ValidationScore = Meter.CreateHistogram<double>(
        "boldmigrate_validation_score",
        unit: "%",
        description: "Distribution of validation accuracy and similarity scores");

    public static readonly Counter<long> ApprovalsTotal = Meter.CreateCounter<long>(
        "boldmigrate_approvals_total",
        description: "Report approvals and flags");

    public static readonly Counter<long> ShimRequestsTotal = Meter.CreateCounter<long>(
        "boldmigrate_shim_requests_total",
        description: "URL compatibility shim requests resolved and missed");
}
