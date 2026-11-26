// Bold Migrate — Step 2 & 3: Report Library & Assessment Dashboard

async function refreshLibraryState() {
  if (!currentRun) return;
  try {
    reportsCache = await Api.getReports(currentRun.id);

    // Default select all non-blocked reports if not yet initialized
    if (selectedReportIds.size === 0 && reportsCache.length > 0) {
      reportsCache.forEach(r => {
        if (r.bucket !== "Blocked" && r.status !== "Excluded") {
          selectedReportIds.add(r.id);
        }
      });
    }

    renderReportLibrary();
    updateSelectedCountUI();
  } catch (err) {
    console.warn("Library refresh failed:", err);
  }
}

function renderReportLibrary() {
  const tbody = document.getElementById("library-table-body");
  if (!tbody) return;

  if (!reportsCache || reportsCache.length === 0) {
    tbody.innerHTML = `
      <tr>
        <td colspan="8" style="text-align:center;color:var(--br-text-muted);padding:32px;">
          <div>No reports found. Connect your SSRS server in Step 1 to discover catalog items.</div>
          <button type="button" class="btn2 btn-sm empty-state-cta" onclick="switchTab('connections')">Go to Step 1: Connect &amp; Discover →</button>
        </td>
      </tr>
    `;
    return;
  }

  const search = (document.getElementById("library-search")?.value || "").toLowerCase().trim();
  const filtered = reportsCache.filter(r => {
    const matchSearch = r.name.toLowerCase().includes(search) || r.folder.toLowerCase().includes(search);
    if (!matchSearch) return false;

    if (currentLibFilter === "all") return true;
    if (currentLibFilter === "clean") return r.bucket === "Clean" || r.bucket === "AutoFixed";
    if (currentLibFilter === "review") return r.bucket === "NeedsReview" || r.bucket === "Blocked";
    return true;
  });

  // Update counts
  const cleanCount = reportsCache.filter(r => r.bucket === "Clean" || r.bucket === "AutoFixed").length;
  const reviewCount = reportsCache.filter(r => r.bucket === "NeedsReview" || r.bucket === "Blocked").length;

  const elAll = document.getElementById("lib-count-all");
  if (elAll) elAll.innerText = reportsCache.length;
  const elClean = document.getElementById("lib-count-clean");
  if (elClean) elClean.innerText = cleanCount;
  const elReview = document.getElementById("lib-count-review");
  if (elReview) elReview.innerText = reviewCount;

  const btnSelectReady = document.getElementById("btn-select-clean-text");
  if (btnSelectReady) btnSelectReady.innerText = `Select Compatible (${cleanCount})`;

  // Update sidebar count
  const sideCount = document.getElementById("side-count-reports");
  if (sideCount) sideCount.innerText = reportsCache.length;

  const footerStatus = document.getElementById("lib-footer-status");
  if (footerStatus) {
    const folderCount = new Set(reportsCache.map(r => r.folder || "/")).size;
    const rLabel = reportsCache.length === 1 ? "report" : "reports";
    const fLabel = folderCount === 1 ? "folder" : "folders";
    footerStatus.innerText = `${reportsCache.length} ${rLabel} cataloged across ${folderCount} ${fLabel}.`;
  }

  tbody.innerHTML = filtered.map(r => {
    const isReview = r.bucket === "NeedsReview" || r.bucket === "Blocked";
    const bucketClass = isReview ? "pill warn" : "pill ok";
    const bucketLabel = isReview ? "Needs Review" : "Compatible";

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
          <input type="checkbox" class="report-checkbox" data-id="${r.id}" ${isSelected ? "checked" : ""} onchange="handleReportCheckboxChange('${r.id}', this.checked)">
        </td>
        <td><strong>${escapeHtml(r.name)}</strong></td>
        <td><code>${escapeHtml(r.folder)}</code></td>
        <td style="color:var(--br-text-muted);">${sizeFormatted}</td>
        <td>${r.parameterCount || 0}</td>
        <td><span class="${bucketClass}">${bucketLabel}</span></td>
        <td style="font-size:12px;color:var(--br-text-secondary);">${blockReasonText}</td>
        <td style="text-align:right;white-space:nowrap;">
          <button class="btn2 btn-sm" data-lib-action="validate" data-report-id="${r.id}">Inspect &amp; Validate</button>
          <button class="btn2 btn-danger btn-sm" data-lib-action="delete" data-report-id="${r.id}" title="Delete this report from Bold Reports destination" style="margin-left:4px;padding:4px 8px;">
            <svg width="12" height="12" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" style="vertical-align:-1px;"><path d="M3 6h18M19 6v14a2 2 0 0 1-2 2H7a2 2 0 0 1-2-2V6m3 0V4a2 2 0 0 1 2-2h4a2 2 0 0 1 2 2v2"></path></svg>
          </button>
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
  if (checked) {
    reportsCache.forEach(r => selectedReportIds.add(r.id));
  } else {
    selectedReportIds.clear();
  }
  document.querySelectorAll(".report-checkbox").forEach(cb => cb.checked = checked);
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

  const masterCb = document.getElementById("lib-select-all");
  if (masterCb) masterCb.checked = count === total && total > 0;

  const btnHeader = document.getElementById("btn-header-migrate-text");
  if (btnHeader) btnHeader.innerText = `Migrate Selected (${count}) →`;

  const btnBar = document.getElementById("btn-bar-migrate-text");
  if (btnBar) btnBar.innerText = `Start Migration (${count}) →`;

  const btnFooter = document.getElementById("btn-footer-migrate-text");
  if (btnFooter) btnFooter.innerText = `Start Migration (${count}) →`;

  const btnDash = document.getElementById("btn-dash-migrate-text");
  if (btnDash) btnDash.innerText = `Start Migration (${count}) →`;

  const btnDashFooter = document.getElementById("btn-dash-footer-migrate");
  if (btnDashFooter) btnDashFooter.innerText = `Start Migration Pipeline (${count}) →`;

  const delBarCount = document.getElementById("del-bar-count");
  if (delBarCount) delBarCount.innerText = count;

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
      switchTab("dashboard");
    },
    "Analyzing RDL schemas, expressions, datasources, and assemblies..."
  );
}

// -----------------------------------------------------------------------------
// Step 3: Assessment Dashboard
// -----------------------------------------------------------------------------
async function refreshDashboardState(knownPoll) {
  if (!currentRun) return;
  try {
    const poll = knownPoll || await Api.pollState(currentRun.id);
    if (!poll) return;

    const total = poll.reportsTotal || 0;
    const clean = poll.reportsClean || 0;
    const autofix = poll.reportsAutoFixed || 0;
    const review = poll.reportsNeedsReview || 0;
    const blocked = poll.reportsBlocked || 0;

    // Real metric tiles
    document.getElementById("dash-tile-found").innerText = total;

    // Readiness score calculation: (Clean + AutoFixed) / Total
    const compatibleCount = clean + autofix;
    const reviewTotal = review + blocked;
    const readinessPct = total > 0 ? Math.round((compatibleCount / total) * 100) : 0;
    document.getElementById("dash-tile-readiness").innerText = `${readinessPct}%`;
    document.getElementById("dash-tile-review").innerText = reviewTotal;
    document.getElementById("dash-tile-blocked").innerText = compatibleCount;

    // Real compatibility breakdown percentages
    const cleanPct = total > 0 ? Math.round((compatibleCount / total) * 100) : 0;
    const reviewPct = total > 0 ? Math.round((reviewTotal / total) * 100) : 0;

    setBarAndPct("comp-clean", cleanPct);
    setBarAndPct("comp-review", reviewPct);

    // Footer must agree with the tile above it.
    const footerReadiness = document.getElementById("dash-footer-readiness");
    if (footerReadiness) {
      footerReadiness.innerHTML = total > 0
        ? `Readiness score: <strong>${readinessPct}%</strong> — ${compatibleCount} of ${total} report(s) compatible for direct migration.`
        : "No reports discovered yet. Run Step 1: Connect & Discover first.";
    }

    // Connection pills reflect the run, not an assumption.
    const srcOk = !!(currentRun && currentRun.sourceServerUrl);
    const dstOk = !!(currentRun && currentRun.destinationServerUrl);
    const srcPill = document.getElementById("dash-ssrs-pill");
    if (srcPill) {
      srcPill.className = srcOk ? "pill ok" : "pill";
      srcPill.innerText = srcOk ? "Connected" : "Not connected";
    }
    const dstPill = document.getElementById("dash-dest-pill");
    if (dstPill) {
      dstPill.className = dstOk ? "pill ok" : "pill";
      dstPill.innerText = dstOk ? "Connected" : "Not connected";
    }
    const srcDetail = document.getElementById("dash-ssrs-detail");
    if (srcDetail) {
      srcDetail.innerText = srcOk
        ? `${currentRun.sourceVersion || "SSRS"} · ${poll.itemsTotal || 0} items`
        : "Source server";
    }

    // Populate Flagged Reports Attention List
    renderFlaggedReportsList();
  } catch (err) {
    console.warn("Dashboard refresh error:", err);
  }
}

function renderFlaggedReportsList() {
  const container = document.getElementById("dash-flagged-list");
  if (!container) return;

  const flagged = reportsCache.filter(r => r.bucket === "NeedsReview" || r.bucket === "Blocked");
  if (flagged.length === 0) {
    container.innerHTML = `
      <div style="padding:16px;text-align:center;color:var(--br-success);font-weight:600;">
        ✓ All reports are 100% compatible and ready for migration!
      </div>
    `;
    return;
  }

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
          <span class="pill warn">Needs Review</span>
          <button class="btn2 btn-sm" onclick="selectReportForValidation('${r.id}')">Inspect</button>
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
        if (host) host.innerHTML = `<span style="color:var(--br-danger-text);font-size:12.5px;">Verification error: ${escapeHtml(err.message)}</span>`;
        showToast("Dependency verification failed: " + err.message);
      }
    },
    "Checking DataSourceReference, SharedDataSetReference, and Subreport targets..."
  );
}

function renderDependencyVerificationResults(summary) {
  const host = document.getElementById("dep-verify-results");
  if (!host || !summary) return;

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
    <div style="display:flex;gap:10px;flex-wrap:wrap;margin-bottom:10px;">
      <span class="pill ok">Healthy Reports: ${summary.healthyReports}/${summary.totalReports}</span>
      <span class="pill ${summary.brokenDataSources > 0 ? "fail" : ""}">Broken DataSources: ${summary.brokenDataSources}</span>
      <span class="pill ${summary.brokenDataSets > 0 ? "fail" : ""}">Broken Datasets: ${summary.brokenDataSets}</span>
      <span class="pill ${summary.brokenSubreports > 0 ? "fail" : ""}">Broken Subreports: ${summary.brokenSubreports}</span>
      ${summary.cyclesDetected > 0 ? `<span class="pill fail">Subreport Cycles: ${summary.cyclesDetected}</span>` : ""}
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
                        ? `<select id="${selectId}" style="height:28px;border:1px solid var(--br-border-strong);border-radius:4px;font-size:11.5px;max-width:210px;">${optionsHtml}</select>
                           <button type="button" class="btn2 btn-sm" style="margin-left:4px;" onclick="rebindBrokenLink('${row.rep.reportId}', '${escapeHtml(row.link.linkType)}', '${escapeHtml(row.link.reference)}', '${selectId}')">Rebind</button>`
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
