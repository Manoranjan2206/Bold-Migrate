using BoldMigrate.Core.Interfaces;
using BoldMigrate.Engine.Orchestrator;
using BoldMigrate.Engine.Transform;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace BoldMigrate.Api.Health;

public class MigrationHealthChecks : IHealthCheck
{
    private readonly IMigrationOrchestrator _orchestrator;
    private readonly IMigrationStore _store;
    private readonly IStagingStore _staging;
    private readonly CompatibilityRulesLoader _rulesLoader;

    public MigrationHealthChecks(
        IMigrationOrchestrator orchestrator,
        IMigrationStore store,
        IStagingStore staging,
        CompatibilityRulesLoader rulesLoader)
    {
        _orchestrator = orchestrator;
        _store = store;
        _staging = staging;
        _rulesLoader = rulesLoader;
    }

    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken ct = default)
    {
        var data = new Dictionary<string, object>();
        var isDegraded = false;
        var issues = new List<string>();

        // 1. boldmigrate.orchestrator
        var hbAge = (DateTime.UtcNow - _orchestrator.LastHeartbeatUtc).TotalSeconds;
        data["orchestrator_heartbeat_age_seconds"] = hbAge;
        if (hbAge > 300)
        {
            isDegraded = true;
            issues.Add("Orchestrator heartbeat is older than 300s.");
        }

        // 2. boldmigrate.store
        try
        {
            var runs = await _store.ListRunsAsync(ct);
            data["store_accessible"] = true;
            data["total_runs"] = runs.Count;
        }
        catch (Exception ex)
        {
            isDegraded = true;
            issues.Add($"Store check failed: {ex.Message}");
            data["store_accessible"] = false;
        }

        // 3. boldmigrate.staging
        try
        {
            var freeSpace = await _staging.GetAvailableSpaceAsync(ct);
            data["staging_free_bytes"] = freeSpace;
            if (freeSpace < 500L * 1024 * 1024)
            {
                isDegraded = true;
                issues.Add("Staging volume has less than 500MB free space.");
            }
        }
        catch (Exception ex)
        {
            isDegraded = true;
            issues.Add($"Staging check failed: {ex.Message}");
        }

        // 4. boldmigrate.rules
        try
        {
            var rules = _rulesLoader.LoadRules();
            data["rules_version"] = rules.Version;
            data["rules_count"] = rules.Rules.Count;
        }
        catch (Exception ex)
        {
            isDegraded = true;
            issues.Add($"Rules load check failed: {ex.Message}");
        }

        // Per spec: A degraded check MUST NOT take the server itself unhealthy.
        if (isDegraded)
        {
            return HealthCheckResult.Degraded(string.Join(" ", issues), data: data);
        }

        return HealthCheckResult.Healthy("All Bold Migrate components healthy.", data: data);
    }
}
