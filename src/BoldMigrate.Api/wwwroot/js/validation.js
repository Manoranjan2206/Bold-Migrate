// Bold Migrate — Step 4: Validation & Preview

let validationSummaryCache = null;
let currentValFilter = "all";
let currentValSearch = "";
let valPollInterval = null;

function selectReportForValidation(reportId) {
  selectedValidationReportId = reportId;
  const r = (reportsCache || []).find(x => x.id === reportId);
  if (r) {
    openInteractiveViewer(r.id, r.path, "staged");
  } else {
    switchTab("validation");
  }
}

async function refreshValidationState() {
  await loadValidationSummary(true);
}

async function loadValidationSummary(force = false) {
  if (!currentRun) return;

  const grid = document.getElementById("val-evidence-grid");
  if (!grid) return;

  try {
    const summary = await Api.getValidationSummary(currentRun.id);
    validationSummaryCache = summary;

    const reports = summary.reports || [];
    const offlineCount = summary.dataSourceOffline || reports.filter(r => r.status === "DataSourceOffline").length || 0;

    // Update Filter Status Counts (Unified Strip)
    const passEl = document.getElementById("val-stat-passed");
    const offEl = document.getElementById("val-stat-offline");
    const manEl = document.getElementById("val-stat-manual");
    const failEl = document.getElementById("val-stat-failed");
    const totEl = document.getElementById("val-stat-total");

    if (passEl) passEl.innerText = summary.passed || 0;
    if (offEl) offEl.innerText = offlineCount;
    if (manEl) manEl.innerText = summary.needsManualCheck || 0;
    if (failEl) failEl.innerText = summary.failed || 0;
    if (totEl) totEl.innerText = summary.total || 0;

    updateValSelectedCountUI();

    const footStatus = document.getElementById("val-footer-status");
    if (footStatus) {
      const readyTotal = (summary.passed || 0) + offlineCount;
      if (readyTotal > 0 && (summary.failed || 0) === 0) {
        footStatus.innerText = `${readyTotal} of ${summary.total} reports verified and ready for upload.`;
      } else if ((summary.failed || 0) > 0) {
        footStatus.innerText = `${readyTotal} reports ready for upload; ${summary.failed} report(s) require review or syntax edit.`;
      } else {
        footStatus.innerText = `Ready to run automated validation on ${summary.total} reports, or skip directly to upload.`;
      }
    }

    renderValidationEvidenceGrid();
  } catch (err) {
    console.error("Failed loading validation summary:", err);
    if (grid) {
      grid.innerHTML = `
        <div class="card" style="grid-column: 1 / -1; padding: 24px; text-align: center; color: var(--br-danger);">
          <p style="margin: 0; font-size: 14px;">Error loading validation summary: ${escapeHtml(err.message)}</p>
          <button class="btn2 btn-sm" onclick="loadValidationSummary(true)" style="margin-top: 10px;">Retry</button>
        </div>`;
    }
  }
}

let currentValViewMode = "details";
let selectedValReportIds = new Set();

function setValidationViewMode(mode) {
  currentValViewMode = mode;
  const detBtn = document.getElementById("val-view-details-btn");
  const crdBtn = document.getElementById("val-view-cards-btn");
  if (detBtn) detBtn.classList.toggle("active", mode === "details");
  if (crdBtn) crdBtn.classList.toggle("active", mode === "cards");
  renderValidationEvidenceGrid();
}

function toggleValActionMenu(btnEl, ev) {
  if (ev) ev.stopPropagation();
  const menu = btnEl?.nextElementSibling;
  const wasOpen = menu?.classList.contains("open");
  document.querySelectorAll(".bm-menu.open").forEach(m => m.classList.remove("open"));
  if (menu && !wasOpen) menu.classList.add("open");
}

if (!window.__bmMenuCloserBound) {
  window.__bmMenuCloserBound = true;
  document.addEventListener("click", (ev) => {
    if (!ev.target.closest(".bm-menu-wrap") || ev.target.closest(".bm-menu-item")) {
      document.querySelectorAll(".bm-menu.open").forEach(m => m.classList.remove("open"));
    }
  });
}

function toggleValReportSelection(reportId, checked) {
  if (checked) selectedValReportIds.add(reportId);
  else selectedValReportIds.delete(reportId);
  updateValSelectedCountUI();
}

function toggleAllValReports(checked) {
  const reports = (validationSummaryCache && validationSummaryCache.reports) ? validationSummaryCache.reports : [];
  if (checked) {
    reports.forEach(r => selectedValReportIds.add(r.reportId));
  } else {
    selectedValReportIds.clear();
  }
  renderValidationEvidenceGrid();
  updateValSelectedCountUI();
}

function updateValSelectedCountUI() {
  const runTextEl = document.getElementById("btn-run-validation-text");
  if (runTextEl) {
    const sel = selectedValReportIds.size;
    const total = validationSummaryCache?.total || 0;
    runTextEl.innerText = sel > 0
      ? `Validate Selected (${sel})`
      : (total > 0 ? `Validate All (${total})` : "Validate All Reports");
  }
}

function startSmartRenderValidation() {
  return startAutomatedRenderValidation(selectedValReportIds.size > 0);
}

function filterValidationCards(filter) {
  currentValFilter = filter;
  ["all", "passed", "offline", "manual", "failed"].forEach(f => {
    const btn = document.getElementById(`val-btn-filter-${f}`);
    if (btn) btn.classList.toggle("active", f === filter);
  });
  renderValidationEvidenceGrid();
}

function onValidationSearch(query) {
  currentValSearch = (query || "").trim().toLowerCase();
  renderValidationEvidenceGrid();
}

function getValidationStatusBadge(r) {
  if (r.status === "Passed" || r.status === "Approved") {
    const dur = r.durationMs ? `${(r.durationMs / 1000).toFixed(1)}s` : "OK";
    return `<span class="pill ok" style="font-size:11px;font-weight:600;">✓ Passed (${dur})</span>`;
  } else if (r.status === "DataSourceOffline") {
    return `<span class="pill" style="background:var(--br-info-bg);color:var(--br-primary);border:1px solid var(--br-info-border);font-size:11px;font-weight:600;">⚡ Data Source Offline</span>`;
  } else if (r.status === "NeedsManualCheck") {
    return `<span class="pill warn" style="font-size:11px;font-weight:600;" title="Requires parameters without defaults">⚠ Needs Parameters</span>`;
  } else if (r.status === "Failed") {
    return `<span class="pill fail" style="font-size:11px;font-weight:600;cursor:pointer;" data-val-action="logs" data-report-id="${r.reportId}" title="Click to view render log">⚠ Render Error</span>`;
  }
  return `<span class="pill" style="font-size:11px;color:var(--br-text-muted);">⚪ Not Validated</span>`;
}

function renderValidationEvidenceGrid() {
  const grid = document.getElementById("val-evidence-grid");
  if (!grid || !validationSummaryCache) return;

  updateValSelectedCountUI();
  const reports = validationSummaryCache.reports || [];
  let filtered = reports;

  if (currentValFilter === "passed") {
    filtered = filtered.filter(r => r.status === "Passed" || r.status === "Approved");
  } else if (currentValFilter === "offline") {
    filtered = filtered.filter(r => r.status === "DataSourceOffline");
  } else if (currentValFilter === "manual") {
    filtered = filtered.filter(r => r.status === "NeedsManualCheck");
  } else if (currentValFilter === "failed") {
    filtered = filtered.filter(r => r.status === "Failed");
  }

  if (currentValSearch) {
    filtered = filtered.filter(r =>
      (r.name && r.name.toLowerCase().includes(currentValSearch)) ||
      (r.path && r.path.toLowerCase().includes(currentValSearch))
    );
  }

  if (filtered.length === 0) {
    grid.style.display = "grid";
    const noReportsAtAll = reports.length === 0;
    grid.innerHTML = `
      <div class="card" style="grid-column: 1 / -1; padding: 36px; text-align: center; color: var(--br-text-muted);">
        <svg width="32" height="32" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="1.5" style="margin-bottom: 8px; opacity: 0.6;">
          <circle cx="11" cy="11" r="8"></circle><line x1="21" y1="21" x2="16.65" y2="16.65"></line>
        </svg>
        <p style="margin: 0; font-size: 14px; font-weight: 500;">${noReportsAtAll ? "No reports discovered yet. Connect your SSRS server in Step 1 first." : `No reports match the current filter (${escapeHtml(currentValFilter)}).`}</p>
        ${noReportsAtAll ? `<button type="button" class="btn2 btn-sm empty-state-cta" onclick="switchTab('connections')">Go to Step 1: Connect &amp; Discover →</button>` : ""}
      </div>`;
    return;
  }

  const buildValActionsHtml = (r) => {
    // Failed renders now carry diagnostic evidence, so Compare is offered for them too: that is
    // where the operator sees the error screen beside the SSRS render and can run the data diff.
    const primaryBtn = r.screenshotUrl
      ? `<button type="button" class="btn2 btn-sm" data-val-action="compare" data-report-id="${escapeHtml(r.reportId)}" title="${r.screenshotIsFailureEvidence ? "See what the viewer showed, beside the SSRS render" : "Compare Source SSRS vs Bold Reports"}">◫ Compare</button>`
      : `<button type="button" class="btn2 btn-sm" data-val-action="viewer" data-report-id="${r.reportId}" title="Preview in interactive local viewer">👁 Preview</button>`;
    const showLogItem = r.status === "Failed" || r.status === "DataSourceOffline";
    return `
      ${primaryBtn}
      <button type="button" class="btn2 btn-sm" data-val-action="designer" data-report-id="${r.reportId}" style="margin-left:4px;" title="Edit report layout">✏ Edit</button>
      <div class="bm-menu-wrap" style="margin-left:4px;">
        <button type="button" class="btn-ghost btn-sm" style="padding:0 8px;height:32px;border:1px solid var(--br-border);" onclick="toggleValActionMenu(this, event)" aria-label="More report actions">···</button>
        <div class="bm-menu" role="menu">
          <button type="button" class="bm-menu-item" role="menuitem" data-val-action="validate" data-report-id="${r.reportId}">▶ Run Visual Check</button>
          <button type="button" class="bm-menu-item" role="menuitem" data-val-action="diff" data-report-id="${r.reportId}">{ } View RDL XML Diff</button>
          ${r.screenshotUrl ? `<button type="button" class="bm-menu-item" role="menuitem" data-val-action="viewer" data-report-id="${r.reportId}">👁 Interactive Viewer</button>` : ""}
          ${showLogItem ? `<button type="button" class="bm-menu-item" role="menuitem" data-val-action="logs" data-report-id="${r.reportId}">📜 View Render Log</button>` : ""}
        </div>
      </div>`;
  };

  if (currentValViewMode === "details") {
    grid.style.display = "block";
    const allChecked = filtered.length > 0 && filtered.every(r => selectedValReportIds.has(r.reportId));
    grid.innerHTML = `
      <div class="card table-scroll" style="padding:0;">
        <table>
          <thead>
            <tr>
              <th class="check-cell"><input type="checkbox" ${allChecked ? "checked" : ""} onchange="toggleAllValReports(this.checked)" title="Select / Deselect All"></th>
              <th>Snapshot</th>
              <th>Report &amp; Path</th>
              <th>Status</th>
              <th style="text-align:right;">Actions</th>
            </tr>
          </thead>
          <tbody>
            ${filtered.map(r => {
              const isSel = selectedValReportIds.has(r.reportId);
              const evidence = r.screenshotIsFailureEvidence === true;
              const thumbCell = r.screenshotUrl
                ? `<img src="${escapeHtml(r.screenshotUrl)}?t=${valRenderVersion}" alt="${evidence ? "Failure evidence for " : ""}${escapeHtml(r.name)}" data-val-action="zoom" data-report-id="${escapeHtml(r.reportId)}" style="width:68px;height:42px;object-fit:cover;object-position:top;border-radius:6px;border:1px solid ${evidence ? "var(--br-danger-border)" : "var(--br-border)"};cursor:pointer;" title="${evidence ? "What the viewer showed when the render failed — click to expand" : "Click to expand snapshot"}">`
                : `<span style="font-size:11px;color:var(--br-text-subtle);">—</span>`;
              const subNote = r.status === "DataSourceOffline"
                ? `<div style="font-size:11.5px;color:var(--br-info-text);margin-top:3px;">RDL valid (DB offline locally)</div>`
                : (r.errorMessage ? `<div style="font-size:11.5px;color:var(--br-warning-text);margin-top:3px;max-width:260px;overflow:hidden;text-overflow:ellipsis;white-space:nowrap;">${escapeHtml(r.errorMessage)}</div>` : "");

              return `
                <tr>
                  <td class="check-cell"><input type="checkbox" ${isSel ? "checked" : ""} onchange="toggleValReportSelection('${r.reportId}', this.checked)"></td>
                  <td>${thumbCell}</td>
                  <td><strong>${escapeHtml(r.name)}</strong><br><small class="mono" style="color:var(--br-text-muted);">${escapeHtml(r.path)}</small></td>
                  <td>${getValidationStatusBadge(r)}${subNote}</td>
                  <td style="text-align:right;white-space:nowrap;">${buildValActionsHtml(r)}</td>
                </tr>
              `;
            }).join("")}
          </tbody>
        </table>
      </div>
    `;
    return;
  }

  grid.style.display = "grid";
  grid.innerHTML = filtered.map(r => {
    const isSel = selectedValReportIds.has(r.reportId);
    const statusBadge = getValidationStatusBadge(r);
    let borderStyle = "border: 1px solid var(--br-border);";
    if (r.status === "Passed" || r.status === "Approved") borderStyle = "border: 1px solid var(--br-success-border);";
    else if (r.status === "DataSourceOffline") borderStyle = "border: 1px solid var(--br-info-border);";
    else if (r.status === "NeedsManualCheck") borderStyle = "border: 1px solid var(--br-warning-border);";
    else if (r.status === "Failed") borderStyle = "border: 1px solid var(--br-danger-border);";

    let thumbHtml = "";
    if (r.screenshotUrl) {
      // A screenshot captured from a FAILED render must never read as a good preview.
      const evidence = r.screenshotIsFailureEvidence === true;
      thumbHtml = `
        <div style="height:150px;width:100%;background:var(--br-surface-alt);border-bottom:1px solid ${evidence ? "var(--br-danger-border)" : "var(--br-border)"};position:relative;overflow:hidden;cursor:pointer;" data-val-action="zoom" data-report-id="${escapeHtml(r.reportId)}">
          <img src="${escapeHtml(r.screenshotUrl)}?t=${valRenderVersion}" alt="${evidence ? "Failure evidence for " : ""}${escapeHtml(r.name)}" style="width:100%;height:100%;object-fit:cover;object-position:top;" loading="lazy">
          ${evidence ? `<div style="position:absolute;top:6px;left:6px;background:var(--br-danger);color:#fff;font-size:10.5px;font-weight:700;padding:2px 6px;border-radius:4px;">What the viewer showed</div>` : ""}
          <div style="position:absolute;bottom:6px;right:6px;background:rgba(15,23,42,0.7);color:var(--br-text-inverse);font-size:10.5px;padding:2px 6px;border-radius:4px;">Expand</div>
        </div>`;
    } else if (r.status === "DataSourceOffline") {
      thumbHtml = `
        <div style="height:125px;width:100%;background:var(--br-info-bg);border-bottom:1px solid var(--br-info-border);display:flex;flex-direction:column;align-items:center;justify-content:center;color:var(--br-info);padding:12px;text-align:center;">
          <span style="font-size:12.5px;font-weight:600;color:var(--br-info-text);">⚡ DB Offline in Local Preview</span>
          <small style="font-size:11px;color:var(--br-info);margin-top:3px;">RDL verified · Ready for server</small>
        </div>`;
    } else if (r.status === "NeedsManualCheck") {
      thumbHtml = `
        <div style="height:125px;width:100%;background:var(--br-warning-bg);border-bottom:1px solid var(--br-warning-border);display:flex;flex-direction:column;align-items:center;justify-content:center;color:var(--br-warning);padding:12px;text-align:center;">
          <span style="font-size:12.5px;font-weight:600;">⚠ Parameters Required</span>
          <small style="font-size:11px;color:var(--br-warning-text);margin-top:3px;">Preview in Viewer to test inputs</small>
        </div>`;
    } else if (r.status === "Failed") {
      thumbHtml = `
        <div style="height:125px;width:100%;background:var(--br-danger-bg);border-bottom:1px solid var(--br-danger-border);display:flex;flex-direction:column;align-items:center;justify-content:center;color:var(--br-danger);padding:12px;text-align:center;cursor:pointer;" data-val-action="logs" data-report-id="${r.reportId}">
          <span style="font-size:12.5px;font-weight:600;">⚠ Render Error</span>
          <small style="font-size:11px;color:var(--br-danger-text);margin-top:3px;max-width:90%;overflow:hidden;text-overflow:ellipsis;white-space:nowrap;">${escapeHtml(r.errorMessage || "Click to inspect log")}</small>
        </div>`;
    } else {
      thumbHtml = `
        <div style="height:110px;width:100%;background:var(--br-surface-alt);border-bottom:1px solid var(--br-border);display:flex;align-items:center;justify-content:center;color:var(--br-text-subtle);font-size:12px;">
          Not checked yet
        </div>`;
    }

    return `
      <div class="card" style="padding:0;overflow:visible;display:flex;flex-direction:column;border-radius:12px;${borderStyle}">
        ${thumbHtml}
        <div style="padding:14px;flex:1;display:flex;flex-direction:column;">
          <div style="display:flex;justify-content:space-between;align-items:flex-start;margin-bottom:6px;gap:8px;">
            <label style="display:flex;align-items:center;gap:6px;cursor:pointer;font-weight:700;font-size:14px;color:var(--br-text);word-break:break-word;">
              <input type="checkbox" ${isSel ? "checked" : ""} onchange="toggleValReportSelection('${r.reportId}', this.checked)">
              <span>${escapeHtml(r.name)}</span>
            </label>
            ${statusBadge}
          </div>
          <div class="mono" style="font-size:11.5px;color:var(--br-text-muted);margin-bottom:10px;">${escapeHtml(r.path)}</div>
          <div style="margin-top:auto;display:flex;justify-content:flex-end;align-items:center;padding-top:10px;border-top:1px solid var(--br-border-subtle);">
            ${buildValActionsHtml(r)}
          </div>
        </div>
      </div>
    `;
  }).join("");
}

function setValidationProgressBanner(visible, title = "", detail = "", pct = 0) {
  const banner = document.getElementById("val-progress-banner");
  if (!banner) return;
  if (!visible) {
    banner.style.display = "none";
    return;
  }
  const tEl = document.getElementById("val-progress-title");
  const dEl = document.getElementById("val-progress-detail");
  const pEl = document.getElementById("val-progress-pct");
  const bEl = document.getElementById("val-progress-bar");
  const clamped = Math.max(0, Math.min(100, Math.round(pct)));
  if (tEl && title) tEl.innerText = title;
  if (dEl) dEl.innerText = detail;
  if (pEl) pEl.innerText = `${clamped}%`;
  if (bEl) bEl.style.width = `${clamped}%`;
  banner.style.display = "block";
}

async function runSingleReportValidation(reportId) {
  if (!currentRun) return;
  const rep = validationSummaryCache?.reports?.find(x => x.reportId === reportId);
  const btn = document.querySelector(`[data-val-action="validate"][data-report-id="${reportId}"]`);
  setButtonLoading(btn, true, "Validating...");
  beginActivity(`val-single-${reportId}`, `Validating '${rep?.name || "Report"}'`, "Launching Playwright render validation...", 20);
  setValidationProgressBanner(true, `Validating '${rep?.name || "Report"}'...`, "Rendering staged report via headless Chromium...", 25);

  try {
    valRenderVersion = Date.now();
    await Api.validateRender(currentRun.id, 1, [reportId]);
    for (let i = 0; i < 12; i++) {
      await new Promise(r => setTimeout(r, 1500));
      valRenderVersion = Date.now();
      await loadValidationSummary();
      const updated = validationSummaryCache?.reports?.find(x => x.reportId === reportId);
      const pct = Math.min(95, 30 + i * 6);
      updateActivity(`val-single-${reportId}`, `Status: ${updated?.status || "Rendering..."}`, pct);
      setValidationProgressBanner(true, `Validating '${rep?.name || "Report"}'...`, `Current status: ${updated?.status || "Rendering..."}`, pct);
      if (i >= 1 && updated && updated.status !== "NotValidated") {
        break;
      }
    }
    showToast(`Validation completed for '${rep?.name || "Report"}'.`);
  } catch (err) {
    showToast("Validation failed to start: " + err.message, true);
  } finally {
    endActivity(`val-single-${reportId}`);
    setButtonLoading(btn, false);
    setValidationProgressBanner(false);
  }
}

async function startAutomatedRenderValidation(selectedOnly = true) {
  if (!currentRun) return;

  const targetIds = selectedOnly ? Array.from(selectedValReportIds) : null;
  if (selectedOnly && (!targetIds || targetIds.length === 0)) {
    showToast("Select one or more reports first, or click 'Run All'.", true);
    return;
  }

  const btnAll = document.getElementById("btn-run-render-validation");
  setButtonLoading(btnAll, true, selectedOnly ? `Validating (${targetIds.length})...` : "Validating All...");

  const targetCount = selectedOnly ? targetIds.length : (validationSummaryCache?.total || 0);
  beginActivity("val-batch", "Automated Render Validation", `Starting validation for ${targetCount || "all"} report(s)...`, 5);
  setValidationProgressBanner(true, `Validating ${targetCount || "all"} report(s) via headless Playwright...`, "Starting worker tabs...", 5);

  try {
    valRenderVersion = Date.now();
    await Api.validateRender(currentRun.id, 5, targetIds);

    if (valPollInterval) clearInterval(valPollInterval);
    let attempts = 0;
    valPollInterval = setInterval(async () => {
      attempts++;
      valRenderVersion = Date.now();
      await loadValidationSummary();

      const summary = validationSummaryCache;
      if (summary) {
        const subset = selectedOnly
          ? summary.reports.filter(r => targetIds.includes(r.reportId))
          : summary.reports;
        const notDone = subset.filter(r => r.status === "NotValidated").length;
        const done = Math.max(0, subset.length - notDone);
        const pct = subset.length > 0 ? Math.min(99, Math.max(10, Math.round((done / subset.length) * 100))) : 50;

        updateActivity("val-batch", `Validated ${done} of ${subset.length} reports (Passed: ${summary.passed}, Errors: ${summary.failed})`, pct);
        setValidationProgressBanner(true, "Automated Playwright Render Validation in progress...",
          `Completed ${done} of ${subset.length} reports · Passed: ${summary.passed} · Offline: ${summary.dataSourceOffline || 0} · Render Errors: ${summary.failed}`, pct);

        // Wait at least 2 poll cycles so re-runs on already-validated reports complete before
        // closing the timer. The old loop also stopped at 40 ticks (~80s) and announced
        // "Validation finished!" even with reports still unvalidated — report that honestly.
        const complete = notDone === 0 && attempts >= 2;
        const gaveUp = attempts >= 300; // ~10 minutes

        if (complete || gaveUp) {
          clearInterval(valPollInterval);
          valPollInterval = null;
          endActivity("val-batch");
          setValidationProgressBanner(false);
          setButtonLoading(btnAll, false);
          updateValSelectedCountUI();

          if (complete) {
            showToast(`Validation finished. Passed: ${summary.passed}, Render Errors: ${summary.failed}, Needs Params: ${summary.needsManualCheck}`);
          } else {
            showToast(`Still validating on the server: ${done} of ${subset.length} done. Reopen this step to see the rest.`, true);
          }
        }
      }
    }, 2000);
  } catch (err) {
    endActivity("val-batch");
    setValidationProgressBanner(false);
    setButtonLoading(btnAll, false);
    updateValSelectedCountUI();
    showToast("Validation failed to start: " + err.message, true);
  }
}

async function skipRenderValidationAndUpload() {
  if (!currentRun) return;
  const confirmed = await showConfirmDialog({
    title: "Skip Visual Check?",
    message: "Skip optional local Playwright rendering and proceed directly to Step 4: Publish & Cutover?",
    confirmText: "Proceed to Publish"
  });
  if (!confirmed) return;

  await withButtonProgress("btn-skip-validation", "Skipping...", "Approving Reports for Upload", async () => {
    try {
      await Api.skipValidation(currentRun.id);
      showToast("Validation skipped. Switching to Step 4: Publish & Cutover...");
      switchTab("publish");
    } catch (err) {
      showToast("Error skipping validation: " + err.message);
    }
  });
}

async function resetValidationResults() {
  if (!currentRun) return;
  const confirmed = await showConfirmDialog({
    title: "Reset Validation Results",
    message: "Clear cached validation results and snapshots? Reports will return to 'Not Validated' until you run validation.",
    confirmText: "Reset Results",
    danger: true
  });
  if (!confirmed) return;
  beginActivity("val-reset", "Resetting Validation Cache", "Clearing cached results and screenshot PNGs...", null);
  try {
    await Api.clearValidationResults(currentRun.id);
    selectedValReportIds.clear();
    valRenderVersion = Date.now();
    await loadValidationSummary(true);
    showToast("Validation results and cached screenshots cleared.");
  } catch (err) {
    showToast("Failed clearing validation results: " + err.message, true);
  } finally {
    endActivity("val-reset");
  }
}

// -----------------------------------------------------------------------------
// Interactive Local & Server Viewer/Designer Modals
// -----------------------------------------------------------------------------

async function bulkApproveQueue() {
  if (!currentRun) return;
  beginActivity("bulk-approve", "Bulk Approving Review Queue", "Approving reports meeting accuracy threshold...", null);
  try {
    const res = await Api.bulkApprove(currentRun.id);
    showToast(`Bulk-approved ${res?.approved ?? 0} report(s) meeting accuracy threshold.`);
    await loadValidationSummary(true);
    await refreshLibraryState();
  } catch (err) {
    showToast("Bulk approve failed: " + err.message);
  } finally {
    endActivity("bulk-approve");
  }
}

async function loadReportRdlDiff(report) {
  if (!currentRun || !report) return;

  const origEl = document.getElementById("rdl-code-original");
  const xfrmEl = document.getElementById("rdl-code-transformed");

  try {
    const data = await Api.getReportRdl(currentRun.id, report.id);
    rdlCache[report.id] = data;

    if (origEl) {
      origEl.innerText = data.originalRdl
        || "The source RDL for this report is not available in staging. Re-run Step 1: Connect & Discover to stage it.";
    }
    if (xfrmEl) {
      xfrmEl.innerText = data.transformedRdl
        || "No transformed RDL was produced. This report was uploaded unchanged, or the assessment in Step 3 has not run yet.";
    }
  } catch (err) {
    if (origEl) origEl.innerText = `Error loading RDL: ${err.message}`;
    if (xfrmEl) xfrmEl.innerText = "";
  }
}

async function approveReportValidation() {
  if (!currentRun || !selectedValidationReportId) return;
  beginActivity("approve-report", "Approving Report", "Recording sign-off decision...", null);
  try {
    await Api.approveReport(currentRun.id, selectedValidationReportId, "Approved via Validation viewer");
    showToast("Report validation approved.");
    closeRdlDiffModal();
    await loadValidationSummary(true);
    await refreshLibraryState();
  } finally {
    endActivity("approve-report");
  }
}

async function flagReportReview() {
  if (!currentRun || !selectedValidationReportId) return;
  beginActivity("flag-report", "Flagging Report for Review", "Updating report status...", null);
  try {
    await Api.flagReport(currentRun.id, selectedValidationReportId, "Flagged for Report Designer inspection");
    showToast("Report flagged for Report Designer review.");
    closeRdlDiffModal();
    await loadValidationSummary(true);
    await refreshLibraryState();
  } finally {
    endActivity("flag-report");
  }
}

// -----------------------------------------------------------------------------
// Step 6: Cutover & Audit
// -----------------------------------------------------------------------------
