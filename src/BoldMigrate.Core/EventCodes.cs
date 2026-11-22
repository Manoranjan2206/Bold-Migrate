namespace BoldMigrate.Core;

/// <summary>
/// Record describing an event code from the system logging specification.
/// </summary>
public record EventDefinition(string Code, string Level, string Description);

/// <summary>
/// Public contract event codes used across logging and telemetry per the Bold Migrate specification.
/// A code MUST NOT change meaning between releases. A code that becomes obsolete is retired, not reused.
/// </summary>
public static class EventCodes
{
    // Run Lifecycle
    public const string RunStart = "RUN-START";
    public const string RunResume = "RUN-RESUME";
    public const string RunStep = "RUN-STEP";
    public const string RunAbandon = "RUN-ABANDON";
    public const string RunComplete = "RUN-COMPLETE";

    // Connection & Discovery
    public const string ConnProbe = "CONN-PROBE";
    public const string ConnOk = "CONN-OK";
    public const string ConnFail = "CONN-FAIL";
    public const string ConnFallback = "CONN-FALLBACK";
    public const string DiscStart = "DISC-START";
    public const string DiscComplete = "DISC-COMPLETE";
    public const string DiscItem = "DISC-ITEM";
    public const string DiscDownload = "DISC-DOWNLOAD";
    public const string DiscSkip = "DISC-SKIP";
    public const string DiscThrottle = "DISC-THROTTLE";

    // Transform
    public const string XfrmStart = "XFRM-START";
    public const string XfrmComplete = "XFRM-COMPLETE";
    public const string XfrmRule = "XFRM-RULE";
    public const string XfrmAttempt = "XFRM-ATTEMPT";
    public const string XfrmGiveup = "XFRM-GIVEUP";
    public const string XfrmBlock = "XFRM-BLOCK";
    public const string XfrmRulesLoad = "XFRM-RULES-LOAD";
    public const string XfrmRulesStale = "XFRM-RULES-STALE";

    // Upload
    public const string UpldStage = "UPLD-STAGE";
    public const string UpldItem = "UPLD-ITEM";
    public const string UpldRetry = "UPLD-RETRY";
    public const string UpldFail = "UPLD-FAIL";
    public const string UpldDuplicate = "UPLD-DUPLICATE";
    public const string UpldPause = "UPLD-PAUSE";
    public const string UpldResume = "UPLD-RESUME";

    // Validation
    public const string ValdCalibrate = "VALD-CALIBRATE";
    public const string ValdFontGap = "VALD-FONT-GAP";
    public const string ValdRender = "VALD-RENDER";
    public const string ValdResult = "VALD-RESULT";
    public const string ValdPageCount = "VALD-PAGECOUNT";
    public const string ValdFail = "VALD-FAIL";
    public const string ValdApprove = "VALD-APPROVE";
    public const string ValdFlag = "VALD-FLAG";

    // Permissions
    public const string PermRead = "PERM-READ";
    public const string PermUnmatched = "PERM-UNMATCHED";
    public const string PermApply = "PERM-APPLY";

    // Cutover
    public const string CutDelta = "CUT-DELTA";
    public const string CutGateWaive = "CUT-GATE-WAIVE";
    public const string CutReport = "CUT-REPORT";
    public const string ShimEnable = "SHIM-ENABLE";
    public const string ShimHit = "SHIM-HIT";
    public const string ShimMiss = "SHIM-MISS";

    // Security
    public const string SecRedact = "SEC-REDACT";
    public const string SecCredClear = "SEC-CRED-CLEAR";

    private static readonly Dictionary<string, EventDefinition> Definitions = new(StringComparer.OrdinalIgnoreCase)
    {
        [RunStart] = new(RunStart, "Information", "A migration run is created"),
        [RunResume] = new(RunResume, "Information", "The orchestrator resumes a run after a restart"),
        [RunStep] = new(RunStep, "Information", "The run moves to a new step"),
        [RunAbandon] = new(RunAbandon, "Warning", "A user abandons a run"),
        [RunComplete] = new(RunComplete, "Information", "Cutover is confirmed"),
        [ConnProbe] = new(ConnProbe, "Information", "API detection runs, with the result"),
        [ConnOk] = new(ConnOk, "Information", "A connection test passes"),
        [ConnFail] = new(ConnFail, "Error", "A connection test fails, with the error code from the catalogue"),
        [ConnFallback] = new(ConnFallback, "Warning", "REST detection fails and SOAP is used"),
        [DiscStart] = new(DiscStart, "Information", "Discovery started"),
        [DiscComplete] = new(DiscComplete, "Information", "Discovery completed"),
        [DiscItem] = new(DiscItem, "Debug", "One item enumerated"),
        [DiscDownload] = new(DiscDownload, "Debug", "One definition staged, with size and hash"),
        [DiscSkip] = new(DiscSkip, "Warning", "An item could not be retrieved after three attempts"),
        [DiscThrottle] = new(DiscThrottle, "Warning", "The source server returned 429 or 503 and parallelism was reduced"),
        [XfrmStart] = new(XfrmStart, "Information", "Analysis started"),
        [XfrmComplete] = new(XfrmComplete, "Information", "Analysis completed, with bucket counts"),
        [XfrmRule] = new(XfrmRule, "Debug", "A rewrite rule fired, with before and after"),
        [XfrmAttempt] = new(XfrmAttempt, "Information", "An auto-fix attempt began, with the attempt number"),
        [XfrmGiveup] = new(XfrmGiveup, "Warning", "Auto-fix exhausted its attempts, with the final error"),
        [XfrmBlock] = new(XfrmBlock, "Warning", "A report was classified as blocked, with the rule code"),
        [XfrmRulesLoad] = new(XfrmRulesLoad, "Information", "The compatibility rules file loaded, with its version"),
        [XfrmRulesStale] = new(XfrmRulesStale, "Warning", "The rules file could not be refreshed and the cached copy is in use"),
        [UpldStage] = new(UpldStage, "Information", "An upload stage began or finished, with counts"),
        [UpldItem] = new(UpldItem, "Debug", "One item uploaded, with the destination identifier"),
        [UpldRetry] = new(UpldRetry, "Warning", "An upload attempt failed and will be retried, with the status code"),
        [UpldFail] = new(UpldFail, "Error", "An item failed after its final attempt"),
        [UpldDuplicate] = new(UpldDuplicate, "Information", "An idempotency key matched and the item was skipped"),
        [UpldPause] = new(UpldPause, "Information", "The user paused"),
        [UpldResume] = new(UpldResume, "Information", "The user resumed"),
        [ValdCalibrate] = new(ValdCalibrate, "Information", "The font calibration pass ran, with its baseline score"),
        [ValdFontGap] = new(ValdFontGap, "Warning", "Calibration fell below 99 percent, naming the unresolved fonts"),
        [ValdRender] = new(ValdRender, "Debug", "A render completed on one server, with format and duration"),
        [ValdResult] = new(ValdResult, "Information", "A comparison completed, with both scores"),
        [ValdPageCount] = new(ValdPageCount, "Warning", "Page counts differed and visual comparison was abandoned"),
        [ValdFail] = new(ValdFail, "Error", "Rendering raised an error on either server"),
        [ValdApprove] = new(ValdApprove, "Information", "A person approved a report"),
        [ValdFlag] = new(ValdFlag, "Information", "A person flagged a report"),
        [PermRead] = new(PermRead, "Information", "Role assignments were read, with counts"),
        [PermUnmatched] = new(PermUnmatched, "Warning", "A principal could not be resolved"),
        [PermApply] = new(PermApply, "Information", "Mappings were applied"),
        [CutDelta] = new(CutDelta, "Information", "A delta sync ran, with items checked and changed"),
        [CutGateWaive] = new(CutGateWaive, "Warning", "A gate was waived, with the reason and the user"),
        [CutReport] = new(CutReport, "Information", "The migration report was generated"),
        [ShimEnable] = new(ShimEnable, "Information", "The URL shim was switched on, with the mapping count"),
        [ShimHit] = new(ShimHit, "Debug", "A request resolved, with source path and destination"),
        [ShimMiss] = new(ShimMiss, "Warning", "A request did not resolve, with the path requested"),
        [SecRedact] = new(SecRedact, "Debug", "A value was redacted before writing"),
        [SecCredClear] = new(SecCredClear, "Information", "Credentials were cleared from memory at run close")
    };

    /// <summary>
    /// Gets the EventDefinition for the specified event code.
    /// </summary>
    public static EventDefinition? GetDefinition(string code) =>
        Definitions.TryGetValue(code, out var def) ? def : null;
}
