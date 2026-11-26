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

    // Update 4 Metric Tiles
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

    // Update Filter Pills
    const pAll = document.getElementById("val-pill-all");
    const pPass = document.getElementById("val-pill-passed");
    const pOff = document.getElementById("val-pill-offline");
    const pMan = document.getElementById("val-pill-manual");
    const pFail = document.getElementById("val-pill-failed");

    if (pAll) pAll.innerText = summary.total || 0;
    if (pPass) pPass.innerText = summary.passed || 0;
    if (pOff) pOff.innerText = offlineCount;
    if (pMan) pMan.innerText = summary.needsManualCheck || 0;
    if (pFail) pFail.innerText = summary.failed || 0;

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
  if (detBtn) detBtn.classList.toggle("filter-active", mode === "details");
  if (crdBtn) crdBtn.classList.toggle("filter-active", mode === "cards");
  renderValidationEvidenceGrid();
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
  const el = document.getElementById("val-selected-count");
  if (el) el.innerText = selectedValReportIds.size;
}

function filterValidationCards(filter) {
  currentValFilter = filter;
  ["all", "passed", "offline", "manual", "failed"].forEach(f => {
    const btn = document.getElementById(`val-btn-filter-${f}`);
    if (btn) {
      if (f === filter) btn.classList.add("filter-active");
      else btn.classList.remove("filter-active");
    }
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
              <th>Report Name</th>
              <th>Status</th>
              <th>Details / Features</th>
              <th style="text-align:right;">Actions</th>
            </tr>
          </thead>
          <tbody>
            ${filtered.map(r => {
              const isSel = selectedValReportIds.has(r.reportId);
              const tagsHtml = (r.featureTags || []).map(t =>
                `<span style="font-size:10.5px;font-weight:600;padding:1px 5px;border-radius:4px;background:var(--br-info-bg);color:var(--br-primary);border:1px solid var(--br-info-border);margin-right:3px;">${escapeHtml(t)}</span>`
              ).join("");
              const thumbCell = r.screenshotUrl
                ? `<img src="${escapeHtml(r.screenshotUrl)}?t=${valRenderVersion}" alt="${escapeHtml(r.name)}" data-val-action="zoom" data-report-id="${r.reportId}" style="width:68px;height:42px;object-fit:cover;object-position:top;border-radius:4px;border:1px solid var(--br-border);cursor:pointer;" title="Click to expand snapshot">`
                : `<span style="font-size:11px;color:var(--br-text-subtle);">—</span>`;
              const detailMsg = r.status === "DataSourceOffline"
                ? `<span style="color:var(--br-info-text);">RDL valid (DB offline in local preview)</span>`
                : (r.errorMessage ? `<span style="color:var(--br-warning-text);">${escapeHtml(r.errorMessage)}</span>` : `<span style="color:var(--br-text-muted);">Ready</span>`);

              return `
                <tr>
                  <td class="check-cell"><input type="checkbox" ${isSel ? "checked" : ""} onchange="toggleValReportSelection('${r.reportId}', this.checked)"></td>
                  <td>${thumbCell}</td>
                  <td><strong>${escapeHtml(r.name)}</strong><br><small style="color:var(--br-text-muted);font-family:var(--br-font-mono);">${escapeHtml(r.path)}</small></td>
                  <td>${getValidationStatusBadge(r)}</td>
                  <td style="font-size:12px;max-width:340px;">${tagsHtml ? `<div style="margin-bottom:3px;">${tagsHtml}</div>` : ""}${detailMsg}</td>
                  <td style="text-align:right;white-space:nowrap;">
                    <button class="btn2 btn-sm" data-val-action="validate" data-report-id="${r.reportId}" title="Run Playwright validation on this report only">▶ Validate</button>
                    <button class="btn2 btn-sm" data-val-action="diff" data-report-id="${r.reportId}" style="margin-left:4px;" title="Compare Original vs Transformed RDL">XML Diff</button>
                    ${r.screenshotUrl ? `<button class="btn2 btn-sm" data-val-action="compare" data-report-id="${r.reportId}" style="margin-left:4px;" title="Visual Swipe Slider & Pixel Diff">◫ Compare</button>` : ""}
                    <button class="btn2 btn-sm" data-val-action="viewer" data-report-id="${r.reportId}" style="margin-left:4px;">👁 View</button>
                    <button class="btn2 btn-sm" data-val-action="designer" data-report-id="${r.reportId}" style="margin-left:4px;">✏ Edit</button>
                  </td>
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
    // Feature tags
    const tagsHtml = (r.featureTags || []).map(t =>
      `<span style="font-size:11px;font-weight:600;padding:2px 7px;border-radius:4px;background:var(--br-info-bg);color:var(--br-primary);border:1px solid var(--br-info-border);">${escapeHtml(t)}</span>`
    ).join(" ");

    // Status Badge & Border
    const statusBadge = getValidationStatusBadge(r);
    let borderStyle = "border: 1px solid var(--br-border);";
    if (r.status === "Passed" || r.status === "Approved") borderStyle = "border: 1px solid var(--br-success-border);";
    else if (r.status === "DataSourceOffline") borderStyle = "border: 1px solid var(--br-info-border);";
    else if (r.status === "NeedsManualCheck") borderStyle = "border: 1px solid var(--br-warning-border);";
    else if (r.status === "Failed") borderStyle = "border: 1px solid var(--br-danger-border);";

    // Thumbnail Preview
    let thumbHtml = "";
    if (r.screenshotUrl) {
      thumbHtml = `
        <div style="height:150px;width:100%;background:var(--br-surface-alt);border-bottom:1px solid var(--br-border);position:relative;overflow:hidden;cursor:pointer;" data-val-action="zoom" data-report-id="${r.reportId}">
          <img src="${escapeHtml(r.screenshotUrl)}?t=${valRenderVersion}" alt="${escapeHtml(r.name)}" style="width:100%;height:100%;object-fit:cover;object-position:top;" loading="lazy">
          <div style="position:absolute;bottom:6px;right:6px;background:rgba(15,23,42,0.7);color:var(--br-text-inverse);font-size:10.5px;padding:2px 6px;border-radius:4px;backdrop-filter:blur(2px);">
            🔍 Click to expand
          </div>
        </div>`;
    } else if (r.status === "DataSourceOffline") {
      thumbHtml = `
        <div style="height:140px;width:100%;background:var(--br-info-bg);border-bottom:1px solid var(--br-info-border);display:flex;flex-direction:column;align-items:center;justify-content:center;color:var(--br-info);padding:12px;text-align:center;">
          <svg width="28" height="28" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="1.8" style="margin-bottom:6px;"><ellipse cx="12" cy="5" rx="9" ry="3"></ellipse><path d="M21 12c0 1.66-4 3-9 3s-9-1.34-9-3"></path><path d="M3 5v14c0 1.66 4 3 9 3s9-1.34 9-3V5"></path></svg>
          <span style="font-size:12.5px;font-weight:600;color:var(--br-info-text);">Data Source Offline</span>
          <small style="font-size:11px;color:var(--br-info);margin-top:2px;">RDL syntax valid • Ready for Server upload</small>
        </div>`;
    } else if (r.status === "NeedsManualCheck") {
      thumbHtml = `
        <div style="height:140px;width:100%;background:var(--br-warning-bg);border-bottom:1px solid var(--br-warning-border);display:flex;flex-direction:column;align-items:center;justify-content:center;color:var(--br-warning);padding:12px;text-align:center;">
          <svg width="28" height="28" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="1.8" style="margin-bottom:6px;"><path d="M12 9v2m0 4h.01m-6.938 4h13.856c1.54 0 2.502-1.667 1.732-3L13.732 4c-.77-1.333-2.694-1.333-3.464 0L3.34 16c-.77 1.333.192 3 1.732 3z"/></svg>
          <span style="font-size:12.5px;font-weight:600;">Requires Parameters</span>
          <small style="font-size:11px;color:var(--br-warning-text);margin-top:2px;">Open in Viewer to supply parameters</small>
        </div>`;
    } else if (r.status === "Failed") {
      thumbHtml = `
        <div style="height:140px;width:100%;background:var(--br-danger-bg);border-bottom:1px solid var(--br-danger-border);display:flex;flex-direction:column;align-items:center;justify-content:center;color:var(--br-danger);padding:12px;text-align:center;cursor:pointer;" data-val-action="logs" data-report-id="${r.reportId}">
          <svg width="28" height="28" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="1.8" style="margin-bottom:6px;"><path d="M12 9v2m0 4h.01m-6.938 4h13.856c1.54 0 2.502-1.667 1.732-3L13.732 4c-.77-1.333-2.694-1.333-3.464 0L3.34 16c-.77 1.333.192 3 1.732 3z"/></svg>
          <span style="font-size:12.5px;font-weight:600;">Render Error</span>
          <small style="font-size:11px;color:var(--br-danger-text);margin-top:2px;max-width:90%;overflow:hidden;text-overflow:ellipsis;white-space:nowrap;">
            ${escapeHtml(r.errorMessage || "Click to inspect log")}
          </small>
        </div>`;
    } else {
      thumbHtml = `
        <div style="height:120px;width:100%;background:var(--br-surface-alt);border-bottom:1px solid var(--br-border);display:flex;flex-direction:column;align-items:center;justify-content:center;color:var(--br-text-subtle);padding:12px;text-align:center;">
          <svg width="28" height="28" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="1.5" style="margin-bottom:6px;"><path d="M14 2H6a2 2 0 0 0-2 2v16a2 2 0 0 0 2 2h12a2 2 0 0 0 2-2V8z"></path><polyline points="14 2 14 8 20 8"></polyline><line x1="16" y1="13" x2="8" y2="13"></line><line x1="16" y1="17" x2="8" y2="17"></line><polyline points="10 9 9 9 8 9"></polyline></svg>
          <span style="font-size:12px;font-weight:500;">Not validated yet</span>
        </div>`;
    }

    return `
      <div class="card" style="padding:0;overflow:hidden;display:flex;flex-direction:column;border-radius:10px;${borderStyle}">
        ${thumbHtml}
        <div style="padding:14px;flex:1;display:flex;flex-direction:column;">
          <div style="display:flex;justify-content:space-between;align-items:flex-start;margin-bottom:6px;gap:8px;">
            <label style="display:flex;align-items:center;gap:6px;cursor:pointer;font-weight:700;font-size:14px;color:var(--br-text);word-break:break-word;">
              <input type="checkbox" ${isSel ? "checked" : ""} onchange="toggleValReportSelection('${r.reportId}', this.checked)">
              <span>${escapeHtml(r.name)}</span>
            </label>
            ${statusBadge}
          </div>
          <div style="font-size:12px;color:var(--br-text-muted);margin-bottom:8px;font-family:var(--br-font-mono);">${escapeHtml(r.path)}</div>

          ${tagsHtml ? `<div style="display:flex;flex-wrap:wrap;gap:4px;margin-bottom:10px;">${tagsHtml}</div>` : ""}

          ${r.status === "DataSourceOffline" ? `
            <div style="font-size:11.5px;color:var(--br-info-text);background:var(--br-info-bg);border:1px solid var(--br-info-border);border-radius:6px;padding:6px 8px;margin-bottom:10px;">
              RDL syntax verified. Database connection is offline in local preview; report is ready for Server upload.
            </div>` : ""}

          ${r.errorMessage && r.status === "Failed" ? `
            <div style="font-size:11.5px;color:var(--br-danger-text);background:var(--br-danger-bg);border:1px solid var(--br-danger-border);border-radius:6px;padding:6px 8px;margin-bottom:10px;max-height:48px;overflow:hidden;text-overflow:ellipsis;">
              ${escapeHtml(r.errorMessage)}
            </div>` : ""}

          <div style="margin-top:auto;display:flex;justify-content:space-between;align-items:center;padding-top:10px;border-top:1px solid var(--br-border-subtle);flex-wrap:wrap;gap:6px;">
            <div style="display:flex;gap:6px;flex-wrap:wrap;">
              <button class="btn2 btn-sm" data-val-action="validate" data-report-id="${r.reportId}" title="Run Playwright validation on this report only">
                ▶ Validate
              </button>
              <button class="btn2 btn-sm" data-val-action="diff" data-report-id="${r.reportId}" title="Compare Original vs Transformed RDL">
                Diff
              </button>
              ${r.screenshotUrl ? `
              <button class="btn2 btn-sm" data-val-action="compare" data-report-id="${r.reportId}" title="Visual Swipe Slider & Pixel Diff">
                ◫ Compare
              </button>` : ""}
              <button class="btn2 btn-sm" data-val-action="viewer" data-report-id="${r.reportId}" title="Open interactive local viewer">
                👁 View
              </button>
              <button class="btn2 btn-sm" data-val-action="designer" data-report-id="${r.reportId}" title="Edit in Web Report Designer">
                ✏ Edit
              </button>
            </div>
            ${r.status === "Failed" || r.status === "DataSourceOffline" ? `
              <button class="btn2 btn-sm" data-val-action="logs" data-report-id="${r.reportId}" style="padding:4px 8px;font-size:11px;color:var(--br-text-muted);">
                📜 Log
              </button>` : ""}
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

  const btnSel = document.getElementById("btn-run-selected-validation");
  const btnAll = document.getElementById("btn-run-render-validation");
  setButtonLoading(btnSel, true, selectedOnly ? `Validating (${targetIds.length})...` : "Validation Active...");
  setButtonLoading(btnAll, true, selectedOnly ? "Busy..." : "Validating All...");

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

        // Wait at least 2 poll cycles so re-runs on already-validated reports complete before closing timer
        if ((notDone === 0 && attempts >= 2) || attempts >= 40) {
          clearInterval(valPollInterval);
          valPollInterval = null;
          endActivity("val-batch");
          setValidationProgressBanner(false);
          setButtonLoading(btnSel, false);
          setButtonLoading(btnAll, false);
          showToast(`Validation finished! Passed: ${summary.passed}, Render Errors: ${summary.failed}, Needs Params: ${summary.needsManualCheck}`);
        }
      }
    }, 2000);
  } catch (err) {
    endActivity("val-batch");
    setValidationProgressBanner(false);
    setButtonLoading(btnSel, false);
    setButtonLoading(btnAll, false);
    showToast("Validation failed to start: " + err.message, true);
  }
}

async function skipRenderValidationAndUpload() {
  if (!currentRun) return;
  const confirmed = await showConfirmDialog({
    title: "Skip Local Validation?",
    message: "Skip local automated validation and proceed directly to Step 5: Upload Pipeline?",
    confirmText: "Proceed to Upload"
  });
  if (!confirmed) return;

  await withButtonProgress("btn-skip-validation", "Skipping...", "Approving Reports for Upload", async () => {
    try {
      await Api.skipValidation(currentRun.id);
      showToast("Validation skipped. Switching to Step 5: Upload Pipeline...");
      switchTab("migration");
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
