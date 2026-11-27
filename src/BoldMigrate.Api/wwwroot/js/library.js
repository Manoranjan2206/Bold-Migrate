// Bold Migrate — Step 2 & 3: Report Library & Assessment Dashboard

// True once the report list has been fetched successfully at least once for this run. The empty
// state must not claim "connect your SSRS server" before we actually know the list is empty.
let libraryLoaded = false;
let libraryLoadError = null;

/// Single source of truth for Step 2. The KPI tiles used to come from Api.pollState() while the
/// table came from Api.getReports(), so a failed getReports left the tiles reading "9 reports,
/// 100% ready" above a table saying "No reports found. Connect your SSRS server in Step 1".
/// Both now derive from one fetch, so they cannot disagree.
async function refreshLibraryState() {
  if (!currentRun) return;
  try {
    reportsCache = await Api.getReports(currentRun.id);
    libraryLoaded = true;
    libraryLoadError = null;

    // Auto-select only on first load: re-selecting every visit silently undid the operator's
    // curation. Blocked reports are never auto-selected — they cannot migrate.
    if (!selectedReportsInitialized && reportsCache.length > 0) {
      selectedReportsInitialized = true;
      reportsCache.forEach(r => {
        if (r.bucket !== "Blocked" && r.status !== "Excluded") {
          selectedReportIds.add(r.id);
        }
      });
    }

    renderReportLibrary();
    renderAssessmentTiles();
    updateSelectedCountUI();
  } catch (err) {
    libraryLoadError = err?.message || "Unknown error";
    console.warn("Library refresh failed:", err);
    renderReportLibrary();
    renderAssessmentTiles();
  }
}

/// Derives the Step 2 KPI tiles from the same reportsCache the table renders, so the headline
/// numbers and the list can never contradict each other.
function renderAssessmentTiles() {
  const total = reportsCache?.length || 0;
  const compatible = (reportsCache || []).filter(r => r.bucket === "Clean" || r.bucket === "AutoFixed").length;
  const review = (reportsCache || []).filter(r => r.bucket === "NeedsReview").length;
  const blocked = (reportsCache || []).filter(r => r.bucket === "Blocked").length;

  const known = libraryLoaded && !libraryLoadError;
  const mainEl = document.getElementById("assess-summary-main");
  const subEl = document.getElementById("assess-summary-sub");
  const hostEl = document.getElementById("assess-summary");
  if (!mainEl || !subEl || !hostEl) return;

  if (!known) {
    hostEl.className = "assess-summary";
    mainEl.innerText = libraryLoadError ? "Assessment unavailable" : "Loading assessment…";
    subEl.innerText = libraryLoadError ? "Could not read the report list from this run." : "";
    renderFlaggedReportsList();
    return;
  }

  if (total === 0) {
    hostEl.className = "assess-summary";
    mainEl.innerText = "No reports discovered yet";
    subEl.innerText = "Run Discover & Assess Catalog in Step 1.";
    renderFlaggedReportsList();
    return;
  }

  const pct = Math.round((compatible / total) * 100);
  hostEl.className = "assess-summary " + (blocked > 0 ? "is-fail" : review > 0 ? "is-warn" : "is-ok");
  mainEl.innerText = `${compatible} of ${total} reports ready to migrate (${pct}%)`;

  const parts = [];
  if (review > 0) parts.push(`${review} need${review === 1 ? "s" : ""} review`);
  if (blocked > 0) parts.push(`${blocked} blocked — cannot migrate`);
  subEl.innerText = parts.length > 0 ? `· ${parts.join(" · ")}` : "· Nothing needs attention.";

  renderFlaggedReportsList();
}

/// The reports currently visible under the active chip + search box. Shared by the table renderer
/// and by select-all so the two can never disagree about what "all" means.
function getFilteredReports() {
  const search = (document.getElementById("library-search")?.value || "").toLowerCase().trim();
  return (reportsCache || []).filter(r => {
    const name = (r.name || "").toLowerCase();
    const folder = (r.folder || "").toLowerCase();
    if (search && !name.includes(search) && !folder.includes(search)) return false;

    if (currentLibFilter === "clean") return r.bucket === "Clean" || r.bucket === "AutoFixed";
    // Blocked is its own filter now: "will never migrate" and "take a look" are opposite
    // outcomes and used to share one chip and one pill.
    if (currentLibFilter === "review") return r.bucket === "NeedsReview";
    if (currentLibFilter === "blocked") return r.bucket === "Blocked";
    return true;
  });
}

function renderReportLibrary() {
  const tbody = document.getElementById("library-table-body");
  if (!tbody) return;

  if (!reportsCache || reportsCache.length === 0) {
    // Three different situations used to render as the same (often wrong) message. A failed
    // fetch is not the same as "you never connected a server".
    let body;
    if (libraryLoadError) {
      body = `
        <div style="color:var(--br-danger-text);font-weight:600;">Couldn't load the report list.</div>
        <div style="margin-top:4px;font-size:12px;">${escapeHtml(libraryLoadError)}</div>
        <button type="button" class="btn2 btn-sm empty-state-cta" data-lib-empty-action="retry">↻ Retry</button>`;
    } else if (!libraryLoaded) {
      body = `<div>Loading discovered reports…</div>`;
    } else {
      body = `
        <div>No reports discovered yet. Run <strong>Discover &amp; Assess Catalog</strong> in Step 1.</div>
        <button type="button" class="btn2 btn-sm empty-state-cta" data-lib-empty-action="connect">Go to Step 1: Source &amp; Target →</button>`;
    }

    tbody.innerHTML = `
      <tr>
        <td colspan="6" style="text-align:center;color:var(--br-text-muted);padding:32px;">${body}</td>
      </tr>
    `;
    return;
  }

  const filtered = getFilteredReports();

  // Update counts
  const cleanCount = reportsCache.filter(r => r.bucket === "Clean" || r.bucket === "AutoFixed").length;
  const reviewCount = reportsCache.filter(r => r.bucket === "NeedsReview").length;
  const blockedCount = reportsCache.filter(r => r.bucket === "Blocked").length;

  const elAll = document.getElementById("lib-count-all");
  if (elAll) elAll.innerText = reportsCache.length;
  const elClean = document.getElementById("lib-count-clean");
  if (elClean) elClean.innerText = cleanCount;
  const elReview = document.getElementById("lib-count-review");
  if (elReview) elReview.innerText = reviewCount;
  const elBlocked = document.getElementById("lib-count-blocked");
  if (elBlocked) elBlocked.innerText = blockedCount;

  const btnSelectReady = document.getElementById("btn-select-clean-text");
  if (btnSelectReady) btnSelectReady.innerText = `Select Compatible (${cleanCount})`;

  const footerStatus = document.getElementById("lib-footer-status");
  if (footerStatus) {
    const folderCount = new Set(reportsCache.map(r => r.folder || "/")).size;
    const rLabel = reportsCache.length === 1 ? "report" : "reports";
    const fLabel = folderCount === 1 ? "folder" : "folders";
    footerStatus.innerText = `${reportsCache.length} ${rLabel} cataloged across ${folderCount} ${fLabel}.`;
  }

  tbody.innerHTML = filtered.map(r => {
    const isBlocked = r.bucket === "Blocked";
    const isReview = r.bucket === "NeedsReview";
    const bucketClass = isBlocked ? "pill fail" : isReview ? "pill warn" : "pill ok";
    const bucketLabel = isBlocked ? "Blocked" : isReview ? "Needs Review" : "Compatible";

    const isSelected = selectedReportIds.has(r.id);
    const sizeFormatted = r.sizeBytes > 1024 ? `${(r.sizeBytes / 1024).toFixed(1)} KB` : `${r.sizeBytes} B`;

    let tagsHtml = "";
    if (r.featureTags) {
      const tags = (typeof r.featureTags === "string" ? r.featureTags.split(",") : r.featureTags).map(t => t.trim()).filter(Boolean);
      tagsHtml = tags.map(t => `<span style="font-size:10.5px;font-weight:600;padding:1px 5px;border-radius:4px;background:var(--br-info-bg);color:var(--br-primary);border:1px solid var(--br-info-border);margin-right:3px;">${escapeHtml(t)}</span>`).join("");
    }
    const reviewMsg = (r.blockReason === "CF-ASSEMBLY" || (r.featureTags && String(r.featureTags).includes("CustomAssembly")))
      ? "Uses custom assemblies — needs review"
      : (r.blockReason ? escapeHtml(r.blockReason) : "Requires review");
    const blockReasonText = isReview
      ? `<span style="color:var(--br-warning-text);font-weight:600;">${reviewMsg}</span>`
      : (tagsHtml ? `${tagsHtml} <span style="color:var(--br-text-muted);">Compatible</span>` : "100% Native RDL");

    return `
      <tr>
        <td class="check-cell">
          <input type="checkbox" class="report-checkbox" data-id="${escapeHtml(r.id)}" ${isSelected ? "checked" : ""} ${isBlocked ? "disabled" : ""}
                 aria-label="Include ${escapeHtml(r.name)} in this migration"
                 title="${isBlocked ? "Blocked reports cannot be migrated" : "Include in this migration"}"
                 data-lib-action="toggle" data-report-id="${escapeHtml(r.id)}">
        </td>
        <td>
          <strong>${escapeHtml(r.name)}</strong>
          <div class="mono" style="font-size:11.5px;color:var(--br-text-muted);">${escapeHtml(r.folder)} · ${sizeFormatted}</div>
        </td>
        <td>${r.parameterCount || 0}</td>
        <td><span class="${bucketClass}">${bucketLabel}</span></td>
        <td style="font-size:12px;color:var(--br-text-secondary);">${blockReasonText}</td>
        <td style="text-align:right;white-space:nowrap;">
          <button type="button" class="btn2 btn-sm" data-lib-action="validate" data-report-id="${escapeHtml(r.id)}">👁 Preview</button>
        </td>
      </tr>
    `;
  }).join("");

  updateSelectedCountUI();
}

function handleReportCheckboxChange(reportId, checked) {
  if (checked) {
    selectedReportIds.add(reportId);
  } else {
    selectedReportIds.delete(reportId);
  }
  updateSelectedCountUI();
}

function toggleSelectAllReports(checked) {
  // Select-all used to ignore the active filter and happily include Blocked reports, which then
  // travelled into validation and upload only to be skipped there.
  const visible = getFilteredReports().filter(r => r.bucket !== "Blocked");
  if (checked) {
    visible.forEach(r => selectedReportIds.add(r.id));
  } else {
    visible.forEach(r => selectedReportIds.delete(r.id));
  }
  document.querySelectorAll(".report-checkbox:not([disabled])").forEach(cb => cb.checked = checked);
  const masterCb = document.getElementById("lib-select-all");
  if (masterCb) masterCb.checked = checked;
  updateSelectedCountUI();
}

function selectAllReady() {
  selectedReportIds.clear();
  reportsCache.forEach(r => {
    if ((r.bucket === "Clean" || r.bucket === "AutoFixed") && r.status !== "Excluded") {
      selectedReportIds.add(r.id);
    }
  });
  renderReportLibrary();
  showToast(`Selected ${selectedReportIds.size} compatible reports for migration.`);
}

function selectAllCleanAndAutoFix() {
  selectAllReady();
}

function updateSelectedCountUI() {
  const count = selectedReportIds.size;
  const total = reportsCache.length;

  const countEl = document.getElementById("lib-selected-count");
  if (countEl) countEl.innerText = count;

  const totalEl = document.getElementById("lib-total-count");
  if (totalEl) totalEl.innerText = total;

  // The master checkbox reflects the selectable rows actually on screen, not the whole cache.
  const selectable = getFilteredReports().filter(r => r.bucket !== "Blocked");
  const masterCb = document.getElementById("lib-select-all");
  if (masterCb) {
    const selectedVisible = selectable.filter(r => selectedReportIds.has(r.id)).length;
    masterCb.checked = selectable.length > 0 && selectedVisible === selectable.length;
    masterCb.indeterminate = selectedVisible > 0 && selectedVisible < selectable.length;
  }

  const btnFooter = document.getElementById("btn-footer-migrate-text");
  if (btnFooter) btnFooter.innerText = `Visual Verify (${count}) →`;

  // The primary CTA used to stay enabled at 0 selected, leading straight to an empty Step 3.
  const btnFooterEl = btnFooter?.closest("button");
  if (btnFooterEl) {
    btnFooterEl.disabled = count === 0;
    btnFooterEl.title = count === 0 ? "Select at least one report to continue" : "";
  }

  const delModalCount = document.getElementById("del-modal-selected-count");
  if (delModalCount) delModalCount.innerText = count;

  const delModalTotal = document.getElementById("del-modal-total-count");
  if (delModalTotal) delModalTotal.innerText = total;

  const selectCleanBtn = document.getElementById("btn-select-clean-text");
  if (selectCleanBtn) {
    const eligible = reportsCache.filter(r => r.bucket === "Clean" || r.bucket === "AutoFixed").length;
    selectCleanBtn.innerText = `Select Clean & Auto-Fixed Only (${eligible})`;
  }
}


async function startMigrationWithSelected() {
  if (!currentRun) return;
  if (selectedReportIds.size === 0) {
    showToast("Please select at least one report to migrate.");
    return;
  }

  await withButtonProgress(
    "btn-header-migrate",
    "Saving Selection...",
    "Preparing Reports for Validation",
    async () => {
      try {
        await Api.selectReports(currentRun.id, Array.from(selectedReportIds));
        switchTab("validation");
        await loadValidationSummary(true);
      } catch (err) {
        showToast("Validation navigation error: " + err.message);
      }
    },
    `Updating ${selectedReportIds.size} selected report(s)...`
  );
}

function filterReportLibrary() {
  renderReportLibrary();
}

function setLibraryFilter(filter, el) {
  currentLibFilter = filter;
  document.querySelectorAll("#library-filter-chips .chip").forEach(c => c.classList.remove("active"));
  const target = el || document.querySelector(`#library-filter-chips .chip[data-filter="${filter}"]`);
  if (target) target.classList.add("active");
  renderReportLibrary();
}

async function runAssessmentAndGoToDashboard() {
  if (!currentRun) return;
  await withButtonProgress(
    "btn-run-assessment",
    "Assessing...",
    "Running Compatibility Assessment",
    async (opId) => {
      await Api.checkEstate(currentRun.id);
      for (let i = 0; i < 15; i++) {
        await new Promise(r => setTimeout(r, 400));
        const poll = await Api.pollState(currentRun.id);
        if (poll && poll.status === "Analysed") break;
        updateActivity(opId, "Evaluating RDL schemas & compatibility rules...", Math.min(90, 20 + i * 5));
      }
      await refreshDashboardState();
      switchTab("assess");
    },
    "Analyzing RDL schemas, expressions, datasources, and assemblies..."
  );
}

// -----------------------------------------------------------------------------
// Step 3: Assessment Dashboard
// -----------------------------------------------------------------------------
/// Kept as the public entry point used by the poller and by switchTab. It no longer reads its own
/// counters from pollState: if the server reports more reports than the table holds, the table is
/// stale (or its fetch failed), so we re-fetch rather than render two disagreeing numbers.
async function refreshDashboardState(knownPoll) {
  if (!currentRun) return;
  try {
    const poll = knownPoll || await Api.pollState(currentRun.id);
    const serverTotal = poll?.reportsTotal || 0;
    const localTotal = reportsCache?.length || 0;

    if (!libraryLoaded || libraryLoadError || serverTotal !== localTotal) {
      await refreshLibraryState();
      return;
    }

    renderAssessmentTiles();
  } catch (err) {
    console.warn("Dashboard refresh error:", err);
  }
}

function renderFlaggedReportsList() {
  const container = document.getElementById("dash-flagged-list");
  if (!container) return;
  const flaggedCard = document.getElementById("dash-flagged-card");

  const flagged = reportsCache.filter(r => r.bucket === "NeedsReview" || r.bucket === "Blocked");
  if (flagged.length === 0) {
    if (flaggedCard) flaggedCard.style.display = "none";
    container.innerHTML = "";
    return;
  }
  if (flaggedCard) flaggedCard.style.display = "block";

  container.innerHTML = flagged.map(r => {
    let desc = (r.blockReason === "CF-ASSEMBLY" || (r.featureTags && String(r.featureTags).includes("CustomAssembly")))
      ? "Uses custom assemblies — needs review"
      : (r.blockReason || "Tagged for review");

    return `
      <div class="flagged-item">
        <div>
          <strong>${escapeHtml(r.name)}</strong>
          <span style="color:var(--br-text-muted);font-size:11.5px;margin-left:6px;">${escapeHtml(r.folder)}</span>
          <div style="font-size:12px;color:var(--br-text-secondary);margin-top:2px;">${escapeHtml(desc)}</div>
        </div>
        <div style="display:flex;align-items:center;gap:8px;">
          <span class="pill ${r.bucket === "Blocked" ? "fail" : "warn"}">${r.bucket === "Blocked" ? "Blocked" : "Needs Review"}</span>
          <button type="button" class="btn2 btn-sm" data-flagged-action="inspect" data-report-id="${escapeHtml(r.id)}">Inspect</button>
        </div>
      </div>
    `;
  }).join("");
}

function setBarAndPct(idPrefix, pct) {
  const pctEl = document.getElementById(`${idPrefix}-pct`);
  const barEl = document.getElementById(`${idPrefix}-bar`);
  if (pctEl) pctEl.innerText = `${pct}%`;
  if (barEl) barEl.style.width = `${pct}%`;
}

async function recheckEstate() {
  if (!currentRun) return;
  await withButtonProgress(
    "btn-recheck-estate",
    "Re-evaluating...",
    "Re-running Compatibility Assessment",
    async () => {
      await Api.checkEstate(currentRun.id);
      for (let i = 0; i < 15; i++) {
        await new Promise(r => setTimeout(r, 400));
        const poll = await Api.pollState(currentRun.id);
        if (poll && poll.status === "Analysed") break;
      }
      await refreshDashboardState();
      showToast("Assessment updated.");
    },
    "Re-evaluating compatibility rules across staged reports..."
  );
}

async function runDependencyVerification() {
  if (!currentRun) return;
  const host = document.getElementById("dep-verify-results");
  await withButtonProgress(
    "btn-verify-links",
    "Verifying Links...",
    "Verifying Subreport & Dataset Links",
    async () => {
      try {
        const res = await Api.verifyDependencies(currentRun.id);
        renderDependencyVerificationResults(res);
        showToast(res.reportsWithIssues > 0
          ? `Link verification found ${res.reportsWithIssues} report(s) with broken links or cycles.`
          : `All ${res.totalReports} report link(s) verified cleanly!`);
      } catch (err) {
        if (host) {
          host.style.display = "block";
          host.innerHTML = `<span style="color:var(--br-danger-text);font-size:12.5px;">Verification error: ${escapeHtml(err.message)}</span>`;
        }
        showToast("Dependency verification failed: " + err.message);
      }
    },
    "Checking DataSourceReference, SharedDataSetReference, and Subreport targets..."
  );
}

function renderDependencyVerificationResults(summary) {
  const host = document.getElementById("dep-verify-results");
  if (!host || !summary) return;
  host.style.display = "block";

  const reports = summary.reports || [];
  const brokenRows = [];
  for (const rep of reports) {
    for (const link of (rep.links || [])) {
      if (!link.resolved || link.isCycle) {
        const candidates = link.linkType === "DataSource"
          ? (summary.availableDataSources || [])
          : link.linkType === "DataSet"
            ? (summary.availableDataSets || [])
            : (summary.availableReports || []).filter(p => p.toLowerCase() !== (rep.path || "").toLowerCase());
        brokenRows.push({ rep, link, candidates });
      }
    }
  }

  host.innerHTML = `
    <div style="display:flex;justify-content:space-between;align-items:center;gap:10px;flex-wrap:wrap;margin-bottom:8px;">
      <div style="display:flex;gap:8px;flex-wrap:wrap;">
        <span class="pill ok">Healthy Reports: ${summary.healthyReports}/${summary.totalReports}</span>
        <span class="pill ${summary.brokenDataSources > 0 ? "fail" : ""}">Broken DataSources: ${summary.brokenDataSources}</span>
        <span class="pill ${summary.brokenDataSets > 0 ? "fail" : ""}">Broken Datasets: ${summary.brokenDataSets}</span>
        <span class="pill ${summary.brokenSubreports > 0 ? "fail" : ""}">Broken Subreports: ${summary.brokenSubreports}</span>
        ${summary.cyclesDetected > 0 ? `<span class="pill fail">Subreport Cycles: ${summary.cyclesDetected}</span>` : ""}
      </div>
      <button type="button" class="btn-ghost btn-sm" data-dep-action="hide">✕ Hide</button>
    </div>
    ${brokenRows.length === 0
      ? `<div style="font-size:12.5px;color:var(--br-success-text);">All shared data sources, shared datasets, and subreport references resolve cleanly across staged items.</div>`
      : `<div class="table-scroll">
          <table>
            <thead><tr><th>Report</th><th>Link Type</th><th>Missing / Broken Reference</th><th>Issue</th><th>Rebind Target</th></tr></thead>
            <tbody>
              ${brokenRows.map((row, idx) => {
        const selectId = `rebind-sel-${idx}`;
        const optionsHtml = row.candidates.length > 0
          ? row.candidates.map(c => `<option value="${escapeHtml(c)}">${escapeHtml(c)}</option>`).join("")
          : `<option value="">No candidates in catalog</option>`;
        return `
                  <tr>
                    <td><strong>${escapeHtml(row.rep.name)}</strong><br><small style="color:var(--br-text-muted);">${escapeHtml(row.rep.path)}</small></td>
                    <td><span class="pill warn" style="font-size:11px;">${escapeHtml(row.link.linkType)}</span></td>
                    <td style="font-family:var(--br-font-mono);font-size:11.5px;">${escapeHtml(row.link.reference)}</td>
                    <td style="font-size:12px;color:var(--br-danger-text);">${escapeHtml(row.link.issue || "Unresolved reference")}</td>
                    <td style="white-space:nowrap;">
                      ${row.candidates.length > 0
            ? `<select id="${escapeHtml(selectId)}" style="height:28px;border:1px solid var(--br-border-strong);border-radius:4px;font-size:11.5px;max-width:210px;">${optionsHtml}</select>
                           <button type="button" class="btn2 btn-sm" style="margin-left:4px;" data-dep-action="rebind" data-report-id="${escapeHtml(row.rep.reportId)}" data-link-type="${escapeHtml(row.link.linkType)}" data-reference="${escapeHtml(row.link.reference)}" data-select-id="${escapeHtml(selectId)}">Rebind</button>`
            : `<span style="font-size:11.5px;color:var(--br-text-muted);">No staged target</span>`}
                    </td>
                  </tr>`;
      }).join("")}
            </tbody>
          </table>
        </div>`}`;
}

async function rebindBrokenLink(reportId, referenceType, oldReference, selectId) {
  if (!currentRun) return;
  const newReference = document.getElementById(selectId)?.value;
  if (!newReference) return;

  try {
    const res = await Api.rebindReference(currentRun.id, reportId, {
      referenceType,
      oldReference,
      newReference
    });
    if (res.success) {
      showToast(res.message || "Reference updated.");
      await runDependencyVerification();
    } else {
      showToast(res.message || "Rebind failed.");
    }
  } catch (err) {
    showToast("Rebind failed: " + err.message);
  }
}

// -----------------------------------------------------------------------------
// Step 5: Migration Pipeline
// -----------------------------------------------------------------------------
