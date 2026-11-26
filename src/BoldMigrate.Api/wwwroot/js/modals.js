// Bold Migrate — Dialogs, Modals, Viewer & Designer

let __activeAppDialogCleanup = null;

function showConfirmDialog({ title = "Confirm Action", message = "", confirmText = "Confirm", cancelText = "Cancel", danger = false } = {}) {
  const modal = document.getElementById("modal-app-dialog");
  const titleEl = document.getElementById("app-dialog-title");
  const msgEl = document.getElementById("app-dialog-message");
  const btnOk = document.getElementById("app-dialog-confirm");
  const btnCancel = document.getElementById("app-dialog-cancel");
  const btnClose = document.getElementById("app-dialog-close");

  if (!modal || !btnOk || !btnCancel) {
    return Promise.resolve(confirm(message));
  }

  if (typeof __activeAppDialogCleanup === "function") __activeAppDialogCleanup(false);

  const prevFocus = document.activeElement;
  titleEl.innerText = title;
  msgEl.innerText = message;
  btnOk.innerText = confirmText;
  btnOk.className = danger ? "btn btn-danger" : "btn";
  btnCancel.style.display = "";
  btnCancel.innerText = cancelText;
  modal.style.display = "flex";
  btnOk.focus();

  return new Promise((resolve) => {
    const finish = (result) => {
      modal.style.display = "none";
      btnOk.removeEventListener("click", onOk);
      btnCancel.removeEventListener("click", onCancel);
      if (btnClose) btnClose.removeEventListener("click", onCancel);
      modal.removeEventListener("click", onBackdrop);
      __activeAppDialogCleanup = null;
      if (prevFocus && typeof prevFocus.focus === "function") prevFocus.focus();
      resolve(result);
    };
    const onOk = () => finish(true);
    const onCancel = () => finish(false);
    const onBackdrop = (ev) => { if (ev.target === modal) finish(false); };

    __activeAppDialogCleanup = finish;
    btnOk.addEventListener("click", onOk);
    btnCancel.addEventListener("click", onCancel);
    if (btnClose) btnClose.addEventListener("click", onCancel);
    modal.addEventListener("click", onBackdrop);
  });
}

function showAlertDialog({ title = "Notice", message = "", okText = "OK", danger = false } = {}) {
  const modal = document.getElementById("modal-app-dialog");
  const titleEl = document.getElementById("app-dialog-title");
  const msgEl = document.getElementById("app-dialog-message");
  const btnOk = document.getElementById("app-dialog-confirm");
  const btnCancel = document.getElementById("app-dialog-cancel");
  const btnClose = document.getElementById("app-dialog-close");

  if (!modal || !btnOk || !btnCancel) {
    alert(message);
    return Promise.resolve();
  }

  if (typeof __activeAppDialogCleanup === "function") __activeAppDialogCleanup(false);

  const prevFocus = document.activeElement;
  titleEl.innerText = title;
  msgEl.innerText = message;
  btnOk.innerText = okText;
  btnOk.className = danger ? "btn btn-danger" : "btn";
  btnCancel.style.display = "none";
  modal.style.display = "flex";
  btnOk.focus();

  return new Promise((resolve) => {
    const finish = () => {
      modal.style.display = "none";
      btnOk.removeEventListener("click", onOk);
      if (btnClose) btnClose.removeEventListener("click", onOk);
      modal.removeEventListener("click", onBackdrop);
      __activeAppDialogCleanup = null;
      if (prevFocus && typeof prevFocus.focus === "function") prevFocus.focus();
      resolve();
    };
    const onOk = () => finish();
    const onBackdrop = (ev) => { if (ev.target === modal) finish(); };

    __activeAppDialogCleanup = finish;
    btnOk.addEventListener("click", onOk);
    if (btnClose) btnClose.addEventListener("click", onOk);
    modal.addEventListener("click", onBackdrop);
  });
}

function bindModalAccessibility() {
  if (document.body.dataset.modalsBound) return;
  document.body.dataset.modalsBound = "1";

  document.addEventListener("keydown", (ev) => {
    if (ev.key !== "Escape") return;
    if (typeof __activeAppDialogCleanup === "function") {
      __activeAppDialogCleanup(false);
      return;
    }
    const orderedClosers = [
      ["modal-visual-compare", closeVisualCompareModal],
      ["modal-snapshot-zoom", closeSnapshotZoom],
      ["modal-render-logs", closeRenderLogsModal],
      ["modal-rdl-diff", closeRdlDiffModal],
      ["modal-interactive-designer", closeInteractiveDesigner],
      ["modal-interactive-viewer", closeInteractiveViewer],
      ["modal-delete-reports", closeDeleteReportsModal]
    ];
    for (const [id, fn] of orderedClosers) {
      const el = document.getElementById(id);
      if (el && el.style.display && el.style.display !== "none") {
        fn();
        return;
      }
    }
  });

  ["modal-delete-reports", "modal-render-logs", "modal-rdl-diff", "modal-visual-compare"].forEach(id => {
    const el = document.getElementById(id);
    if (!el) return;
    el.addEventListener("click", (ev) => {
      if (ev.target !== el) return;
      if (id === "modal-delete-reports") closeDeleteReportsModal();
      else if (id === "modal-render-logs") closeRenderLogsModal();
      else if (id === "modal-rdl-diff") closeRdlDiffModal();
      else if (id === "modal-visual-compare") closeVisualCompareModal();
    });
  });
}

function confirmRescanModal(poll) {
  const modal = document.getElementById("modal-confirm-rescan");
  const msgEl = document.getElementById("rescan-modal-msg");
  const btnConfirm = document.getElementById("btn-rescan-confirm");
  const btnCancel = document.getElementById("btn-rescan-cancel");
  const btnClose = document.getElementById("btn-rescan-close");

  if (!modal || !btnConfirm || !btnCancel) {
    return showConfirmDialog({
      title: "Re-fetch SSRS Catalog?",
      message: "We already fetched the SSRS catalog. If you fetch again, you may lose modified reports, approvals, or validation results. Do you want to rerun?",
      confirmText: "Yes, Rerun Fresh"
    });
  }

  if (msgEl && poll) {
    msgEl.innerText = `We already fetched the SSRS catalog (${poll.itemsTotal} items, ${poll.reportsTotal} reports). `
      + "If you fetch again, you may lose modified reports, approvals, validation results, or selections. "
      + "Please confirm — do you want to rerun?";
  }

  modal.style.display = "flex";

  return new Promise(resolve => {
    const cleanup = (result) => {
      modal.style.display = "none";
      btnConfirm.removeEventListener("click", onConfirm);
      btnCancel.removeEventListener("click", onCancel);
      if (btnClose) btnClose.removeEventListener("click", onCancel);
      modal.removeEventListener("click", onOverlay);
      document.removeEventListener("keydown", onKey);
      resolve(result);
    };
    const onConfirm = () => cleanup(true);
    const onCancel = () => cleanup(false);
    const onOverlay = (e) => { if (e.target === modal) cleanup(false); };
    const onKey = (e) => { if (e.key === "Escape") cleanup(false); };

    btnConfirm.addEventListener("click", onConfirm);
    btnCancel.addEventListener("click", onCancel);
    if (btnClose) btnClose.addEventListener("click", onCancel);
    modal.addEventListener("click", onOverlay);
    document.addEventListener("keydown", onKey);
  });
}


let sectionsCache = [];

async function openDeleteReportsModal(initialScope = 'all') {
  if (!currentRun) {
    showToast("No active migration run.");
    return;
  }

  const modal = document.getElementById("modal-delete-reports");
  if (!modal) return;

  // Set total count
  const totalEl = document.getElementById("del-modal-total-count");
  if (totalEl) totalEl.innerText = reportsCache.length;

  const selEl = document.getElementById("del-modal-selected-count");
  if (selEl) selEl.innerText = selectedReportIds.size;

  // Clear previous status messages
  const statusEl = document.getElementById("del-status-message");
  if (statusEl) {
    statusEl.style.display = "none";
    statusEl.innerHTML = "";
  }

  // Set radio
  const radioAll = document.getElementById("del-scope-all");
  const radioSection = document.getElementById("del-scope-section");
  const radioSelected = document.getElementById("del-scope-selected");

  if (initialScope === 'selected' && selectedReportIds.size > 0 && radioSelected) {
    radioSelected.checked = true;
  } else if (initialScope === 'section' && radioSection) {
    radioSection.checked = true;
  } else if (radioAll) {
    radioAll.checked = true;
  }

  toggleDeleteScopeView();
  modal.style.display = "flex";

  // Load sections from backend
  await loadSectionsForDeleteModal();
}

function closeDeleteReportsModal() {
  const modal = document.getElementById("modal-delete-reports");
  if (modal) modal.style.display = "none";
}

function toggleDeleteScopeView() {
  const isSection = document.getElementById("del-scope-section")?.checked;
  const isSelected = document.getElementById("del-scope-selected")?.checked;
  const secBox = document.getElementById("del-sections-box");
  if (secBox) secBox.style.display = isSection ? "block" : "none";

  const confirmText = document.getElementById("btn-del-confirm-text");
  if (confirmText) {
    if (isSelected) {
      confirmText.innerText = `Delete ${selectedReportIds.size} Selected Reports in Bold Reports`;
    } else if (isSection) {
      confirmText.innerText = "Delete Selected Sections in Bold Reports";
    } else {
      confirmText.innerText = `Delete All (${reportsCache.length}) Reports in Bold Reports`;
    }
  }
}

async function loadSectionsForDeleteModal() {
  const listEl = document.getElementById("del-sections-list");
  if (!listEl || !currentRun) return;

  try {
    sectionsCache = await Api.getSections(currentRun.id);
    if (!sectionsCache || sectionsCache.length === 0) {
      const grouped = {};
      reportsCache.forEach(r => {
        const sec = r.folder || "/";
        grouped[sec] = (grouped[sec] || 0) + 1;
      });
      sectionsCache = Object.keys(grouped).sort().map(k => ({
        section: k,
        totalReports: grouped[k],
        uploadedReports: 0
      }));
    }

    listEl.innerHTML = sectionsCache.map((s, idx) => `
      <label class="del-section-item">
        <span style="display:flex;align-items:center;gap:8px;">
          <input type="checkbox" class="del-section-cb" value="${escapeHtml(s.section)}" checked>
          <strong>${escapeHtml(s.section)}</strong>
        </span>
        <span class="pill" style="font-size:11px;background:var(--br-surface-hover);color:var(--br-text-secondary);">
          ${s.totalReports} report${s.totalReports === 1 ? '' : 's'}
        </span>
      </label>
    `).join("");
  } catch (err) {
    listEl.innerHTML = `<div style="color:var(--br-danger);font-size:12px;padding:6px;">Failed to load sections: ${err.message}</div>`;
  }
}

function toggleAllDeleteSections(checked) {
  document.querySelectorAll(".del-section-cb").forEach(cb => cb.checked = checked);
}

async function executeDeleteReports() {
  if (!currentRun) return;

  const btnConfirm = document.getElementById("btn-del-confirm");
  const btnCancel = document.getElementById("btn-del-cancel");
  const confirmText = document.getElementById("btn-del-confirm-text");
  const statusEl = document.getElementById("del-status-message");

  const isAll = document.getElementById("del-scope-all")?.checked;
  const isSection = document.getElementById("del-scope-section")?.checked;
  const isSelected = document.getElementById("del-scope-selected")?.checked;
  const deleteCategories = document.getElementById("del-option-categories")?.checked || false;

  let options = {};
  if (isAll) {
    options = { deleteAll: true, deleteCategories };
  } else if (isSection) {
    const checkedSections = Array.from(document.querySelectorAll(".del-section-cb:checked")).map(cb => cb.value);
    if (checkedSections.length === 0) {
      await showAlertDialog({ title: "No Section Selected", message: "Please select at least one section to delete." });
      return;
    }
    options = { sections: checkedSections, deleteCategories };
  } else if (isSelected) {
    if (selectedReportIds.size === 0) {
      await showAlertDialog({ title: "No Reports Selected", message: "No reports are currently selected in the Report Library." });
      return;
    }
    options = { reportIds: Array.from(selectedReportIds) };
  }

  // Confirmation prompt
  const scopeMsg = isAll ? `ALL ${reportsCache.length} reports` :
                   isSection ? `${options.sections.length} section(s)` :
                   `${selectedReportIds.size} selected report(s)`;

  const confirmed = await showConfirmDialog({
    title: "Delete Reports from Bold Reports",
    message: `Are you sure you want to delete ${scopeMsg} from Bold Reports Server?\n\nThis will remove them from the destination server and reset their local migration state to Analysed.`,
    confirmText: "Permanently Delete",
    danger: true
  });
  if (!confirmed) return;

  // Update button state
  if (btnConfirm) btnConfirm.disabled = true;
  if (btnCancel) btnCancel.disabled = true;
  if (confirmText) confirmText.innerText = "Deleting from Bold Reports...";
  if (statusEl) {
    statusEl.style.display = "block";
    statusEl.style.background = "var(--br-info-bg)";
    statusEl.style.color = "var(--br-info-text)";
    statusEl.style.border = "1px solid var(--br-info-border)";
    statusEl.innerHTML = `<strong>Processing:</strong> Deleting ${scopeMsg} from Bold Reports destination...`;
  }

  try {
    const res = await Api.deleteDestinationReports(currentRun.id, options);
    if (res.success) {
      if (statusEl) {
        statusEl.style.background = "var(--br-success-bg)";
        statusEl.style.color = "var(--br-success-text)";
        statusEl.style.border = "1px solid var(--br-success-border)";
        statusEl.innerHTML = `<strong>Success:</strong> ${res.message}`;
      }
      showToast(res.message);

      // Refresh Report Library and Estate counts
      await refreshLibraryState();
      await refreshConnectionsState();

      // Close modal after 1.5 seconds
      setTimeout(() => {
        closeDeleteReportsModal();
        if (btnConfirm) btnConfirm.disabled = false;
        if (btnCancel) btnCancel.disabled = false;
        if (confirmText) confirmText.innerText = "Permanently Delete from Bold Reports";
      }, 1500);
    } else {
      throw new Error(res.message || "Failed to delete reports.");
    }
  } catch (err) {
    if (statusEl) {
      statusEl.style.background = "var(--br-danger-bg)";
      statusEl.style.color = "var(--br-danger-text)";
      statusEl.style.border = "1px solid var(--br-danger-border)";
      statusEl.innerHTML = `<strong>Error:</strong> ${err.message}`;
    }
    showToast("Delete failed: " + err.message);
    if (btnConfirm) btnConfirm.disabled = false;
    if (btnCancel) btnCancel.disabled = false;
    if (confirmText) confirmText.innerText = "Permanently Delete from Bold Reports";
  }
}

async function deleteSingleBoldReport(reportId, reportName) {
  if (!currentRun) return;
  const confirmed = await showConfirmDialog({
    title: "Delete Report from Bold Reports",
    message: `Delete report '${reportName}' from Bold Reports destination?\n\nThis will remove it from the Bold Reports server and reset its status to Analysed.`,
    confirmText: "Delete Report",
    danger: true
  });
  if (!confirmed) return;

  showToast(`Deleting '${reportName}' from Bold Reports...`);
  try {
    const res = await Api.deleteDestinationReports(currentRun.id, { reportIds: [reportId] });
    if (res.success) {
      showToast(`Deleted '${reportName}' from Bold Reports.`);
      await refreshLibraryState();
      await refreshConnectionsState();
    } else {
      showToast("Delete failed: " + res.message);
    }
  } catch (err) {
    showToast("Delete error: " + err.message);
  }
}


function openInteractiveViewer(reportId, reportPath, mode = "staged") {
  window.__activeInspectReport = { reportId, reportPath, mode };
  const modal = document.getElementById("modal-interactive-viewer");
  const host = document.getElementById("interactive-viewer-host");
  const title = document.getElementById("interactive-viewer-title");
  const pill = document.getElementById("interactive-viewer-mode-pill");

  if (!modal || !host) return;

  title.innerText = `Report Viewer — ${reportPath || reportId}`;
  pill.innerText = mode === "staged" ? "Local Staged Engine" : "Bold Reports Server";
  pill.className = mode === "staged" ? "pill ok" : "pill warn";

  modal.style.display = "flex";

  // Destroy previous viewer if any
  try { $(host).boldReportViewer("destroy"); } catch {}
  host.innerHTML = "";

  if (mode === "staged") {
    $(host).boldReportViewer({
      reportServiceUrl: "/api/preview/runs/" + currentRun.id + "/viewer",
      reportPath: reportPath || reportId
    });
  } else {
    // Destination Server mode
    const destUrl = currentRun?.destinationServerUrl || destinationSessionCache.destinationUrl;
    if (!destUrl) {
      showToast("Bold Reports destination URL is not configured.");
      return;
    }
    const serviceUrl = destUrl.replace(/\/site\/[^/]+/, "") + "/reporting/reportservice/api/Viewer";
    $(host).boldReportViewer({
      reportServiceUrl: serviceUrl,
      reportServerUrl: destUrl,
      reportPath: reportPath,
      serviceAuthorizationToken: destinationSessionCache.bearerToken ? `bearer ${destinationSessionCache.bearerToken}` : ""
    });
  }
}

function closeInteractiveViewer() {
  const modal = document.getElementById("modal-interactive-viewer");
  const host = document.getElementById("interactive-viewer-host");
  if (modal) modal.style.display = "none";
  if (host) {
    try { $(host).boldReportViewer("destroy"); } catch {}
    host.innerHTML = "";
  }
}

function switchViewerToDesigner() {
  const active = window.__activeInspectReport;
  closeInteractiveViewer();
  if (active) {
    openInteractiveDesigner(active.reportId, active.reportPath, active.mode || "staged");
  }
}

function openInteractiveDesigner(reportId, reportPath, mode = "staged") {
  const modal = document.getElementById("modal-interactive-designer");
  const host = document.getElementById("interactive-designer-host");
  const title = document.getElementById("interactive-designer-title");
  const pill = document.getElementById("interactive-designer-mode-pill");

  if (!modal || !host) return;

  const targetPath = reportPath || reportId;
  title.innerText = `Report Designer — ${targetPath}`;
  pill.innerText = mode === "staged" ? "Local Staged Engine" : "Bold Reports Server";
  pill.className = mode === "staged" ? "pill ok" : "pill warn";

  modal.style.display = "flex";

  try { $(host).boldReportDesigner("destroy"); } catch {}
  host.innerHTML = "";

  if (mode === "staged") {
    $(host).boldReportDesigner({
      serviceUrl: "/api/preview/runs/" + currentRun.id + "/designer",
      create: function () {
        const designer = $(host).data("boldReportDesigner");
        if (designer && targetPath) {
          designer.openReport(targetPath);
        }
      },
      reportSaved: function () {
        showToast("Report changes saved to local staging.");
      }
    });
  } else {
    // Destination Server mode
    const destUrl = currentRun?.destinationServerUrl || destinationSessionCache.destinationUrl;
    if (!destUrl) {
      showToast("Bold Reports destination URL is not configured.");
      return;
    }
    const serviceUrl = destUrl.replace(/\/site\/[^/]+/, "") + "/reporting/reportservice/api/Designer";
    $(host).boldReportDesigner({
      serviceUrl: serviceUrl,
      serverUrl: destUrl,
      serviceAuthorizationToken: destinationSessionCache.bearerToken ? `bearer ${destinationSessionCache.bearerToken}` : "",
      create: function () {
        const designer = $(host).data("boldReportDesigner");
        if (designer && targetPath) {
          designer.openReport(targetPath);
        }
      }
    });
  }
}

function closeInteractiveDesigner() {
  const modal = document.getElementById("modal-interactive-designer");
  const host = document.getElementById("interactive-designer-host");
  if (modal) modal.style.display = "none";
  if (host) {
    try { $(host).boldReportDesigner("destroy"); } catch {}
    host.innerHTML = "";
  }
  loadValidationSummary(true);
}

async function openRenderLogs(reportPath) {
  const modal = document.getElementById("modal-render-logs");
  const body = document.getElementById("render-logs-body");
  const title = document.getElementById("render-logs-title");

  if (!modal || !body) return;
  title.innerText = `Diagnostic Error Logs: ${reportPath || "Report"}`;
  body.innerHTML = "<p style='color:var(--br-text-muted);'>Loading error logs...</p>";
  modal.style.display = "flex";

  try {
    const logs = await Api.getRenderLogs(currentRun.id, reportPath);
    if (!logs || logs.length === 0) {
      body.innerHTML = `
        <div style="padding:20px;text-align:center;color:var(--br-text-muted);">
          <p style="margin:0;font-size:14px;">No internal runtime errors logged for this report.</p>
        </div>`;
      return;
    }

    body.innerHTML = logs.map(e => `
      <div style="background:var(--br-danger-bg);border:1px solid var(--br-danger-border);border-radius:8px;padding:12px;margin-bottom:12px;">
        <div style="display:flex;justify-content:space-between;margin-bottom:6px;">
          <strong style="color:var(--br-danger);font-size:13px;">${escapeHtml(e.errorType || "Error")} — ${escapeHtml(e.methodName || "Render")}</strong>
          <small style="color:var(--br-text-muted);">${new Date(e.timestampUtc).toLocaleTimeString()}</small>
        </div>
        <div style="font-size:13px;color:var(--br-danger-text);margin-bottom:4px;font-weight:500;">${escapeHtml(e.message)}</div>
        ${e.exceptionMessage ? `<div style="font-size:12px;color:var(--br-danger-text);margin-bottom:4px;font-family:monospace;">Exception: ${escapeHtml(e.exceptionMessage)}</div>` : ""}
        ${e.stackTrace ? `<pre style="font-size:11px;background:var(--br-surface);border:1px solid var(--br-danger-border);border-radius:4px;padding:8px;max-height:140px;overflow:auto;margin:6px 0 0 0;color:var(--br-text-secondary);">${escapeHtml(e.stackTrace)}</pre>` : ""}
      </div>
    `).join("");
  } catch (err) {
    body.innerHTML = `<p style="color:var(--br-danger);">Failed loading error logs: ${escapeHtml(err.message)}</p>`;
  }
}

function closeRenderLogsModal() {
  const modal = document.getElementById("modal-render-logs");
  if (modal) modal.style.display = "none";
}

function openSnapshotZoom(imgSrc, title) {
  const modal = document.getElementById("modal-snapshot-zoom");
  const img = document.getElementById("snapshot-zoom-img");
  const t = document.getElementById("snapshot-zoom-title");
  if (modal && img) {
    img.src = imgSrc;
    if (t) t.innerText = `Visual Snapshot — ${title || ""}`;
    modal.style.display = "flex";
  }
}

function closeSnapshotZoom() {
  const modal = document.getElementById("modal-snapshot-zoom");
  if (modal) modal.style.display = "none";
}

// -----------------------------------------------------------------------------
// Destination Uploaded Reports: Server-Integration Actions (Step 5)
// -----------------------------------------------------------------------------
async function loadServerUploadedReports() {
  if (!currentRun) return;
  const tbody = document.getElementById("server-uploaded-reports-tbody");
  if (!tbody) return;

  try {
    const res = await Api.getReports(currentRun.id);
    const list = Array.isArray(res) ? res : (res?.reports || []);
    reportsCache = list;
    const uploaded = list.filter(r => r.status === "Uploaded" || r.state === "Uploaded" || r.uploaded);

    if (uploaded.length === 0) {
      tbody.innerHTML = `<tr><td colspan="4" style="text-align:center;color:var(--br-text-muted);padding:16px;">No reports have completed upload to Bold Reports Server yet.</td></tr>`;
      return;
    }

    tbody.innerHTML = uploaded.map(r => {
      const cat = r.folder ? r.folder.replace(/^\//, "") : "General";
      return `
        <tr>
          <td><strong>${escapeHtml(r.name)}</strong></td>
          <td><code>${escapeHtml(cat)}</code></td>
          <td><span class="pill ok">Uploaded</span></td>
          <td style="text-align:right;white-space:nowrap;">
            <button class="btn2 btn-sm" data-srv-action="viewer" data-report-id="${r.id}" title="View directly on destination Bold Reports Server">
              View on Server
            </button>
            <button class="btn btn-sm" data-srv-action="designer" data-report-id="${r.id}" style="margin-left:6px;" title="Edit directly on destination Bold Reports Server">
              Edit on Server
            </button>
          </td>
        </tr>
      `;
    }).join("");
  } catch (err) {
    console.error("Failed loading uploaded server reports:", err);
  }
}

async function openRdlDiffModal(reportId) {
  if (!currentRun || !reportId) return;
  selectedValidationReportId = reportId;
  const modal = document.getElementById("modal-rdl-diff");
  const titleEl = document.getElementById("rdl-diff-title");
  const bucketEl = document.getElementById("rdl-diff-bucket");
  const origEl = document.getElementById("rdl-code-original");
  const xfrmEl = document.getElementById("rdl-code-transformed");

  const rep = validationSummaryCache?.reports?.find(x => x.reportId === reportId)
    || (reportsCache || []).find(x => x.id === reportId);
  if (titleEl) titleEl.innerText = `RDL Source vs Transformed — ${rep?.name || reportId}`;
  if (origEl) origEl.innerText = "Loading RDL...";
  if (xfrmEl) xfrmEl.innerText = "Loading RDL...";
  if (modal) modal.style.display = "flex";

  await loadReportRdlDiff({ id: reportId, bucket: rep?.bucket });
  if (bucketEl && rdlCache[reportId]?.bucket) {
    bucketEl.innerText = rdlCache[reportId].bucket;
    bucketEl.className = rdlCache[reportId].bucket === "NeedsReview" ? "pill warn" : "pill ok";
  }
}

function closeRdlDiffModal() {
  const modal = document.getElementById("modal-rdl-diff");
  if (modal) modal.style.display = "none";
}

// -----------------------------------------------------------------------------
// Visual Comparison Slider & Pixel-Diff Engine (Step 4)
// -----------------------------------------------------------------------------
let __cmpActiveReportId = null;
let __cmpAfterSrc = "";
let __cmpBeforeSrc = "";

function openVisualCompareModal(reportId, refreshSource = false) {
  if (!currentRun || !reportId) return;
  const r = validationSummaryCache?.reports?.find(x => x.reportId === reportId);
  if (!r || !r.screenshotUrl) {
    showToast("Run Step 4 render validation on this report first to capture its snapshot.");
    return;
  }

  __cmpActiveReportId = reportId;
  __cmpAfterSrc = `${r.screenshotUrl}?t=${valRenderVersion}`;
  __cmpBeforeSrc = `/api/migration/runs/${currentRun.id}/screenshots/${reportId}/source.png${refreshSource ? `?refresh=true&t=${Date.now()}` : `?t=${valRenderVersion}`}`;

  const modal = document.getElementById("modal-visual-compare");
  const titleEl = document.getElementById("cmp-modal-title");
  if (titleEl) titleEl.innerText = `Visual Comparison — ${r.name}`;

  setCompareMode("swipe");
  onCompareSliderInput(50);
  const rangeEl = document.getElementById("cmp-swipe-range");
  if (rangeEl) rangeEl.value = "50";

  loadCompareImages(__cmpBeforeSrc, __cmpAfterSrc);
  if (modal) modal.style.display = "flex";
}

function closeVisualCompareModal() {
  const modal = document.getElementById("modal-visual-compare");
  if (modal) modal.style.display = "none";
}

function setCompareMode(mode) {
  const modes = ["swipe", "side", "diff"];
  for (const m of modes) {
    const pane = document.getElementById(`cmp-pane-${m}`);
    const btn = document.getElementById(`cmp-mode-${m}`);
    if (pane) pane.style.display = m === mode ? "block" : "none";
    if (btn) btn.classList.toggle("active", m === mode);
  }
}

function onCompareSliderInput(val) {
  const pct = Math.max(0, Math.min(100, Number(val) || 0));
  const topEl = document.getElementById("cmp-swipe-top");
  const divEl = document.getElementById("cmp-swipe-divider");
  if (topEl) topEl.style.clipPath = `inset(0 ${100 - pct}% 0 0)`;
  if (divEl) divEl.style.left = `${pct}%`;
}

function retryCaptureSourceSnapshot() {
  if (!__cmpActiveReportId) return;
  openVisualCompareModal(__cmpActiveReportId, true);
}

function loadCompareReferenceFile(event) {
  const file = event.target?.files?.[0];
  if (!file) return;
  const reader = new FileReader();
  reader.onload = () => {
    __cmpBeforeSrc = String(reader.result || "");
    loadCompareImages(__cmpBeforeSrc, __cmpAfterSrc, true);
  };
  reader.readAsDataURL(file);
}

function loadCompareImages(beforeSrc, afterSrc, isCustomRef = false) {
  const afterImg = document.getElementById("cmp-after-img");
  const beforeImg = document.getElementById("cmp-before-img");
  const sideAfter = document.getElementById("cmp-side-after");
  const sideBefore = document.getElementById("cmp-side-before");
  const noticeEl = document.getElementById("cmp-source-notice");
  const pillEl = document.getElementById("cmp-similarity-pill");

  if (!afterImg || !beforeImg) return;
  if (pillEl) {
    pillEl.className = "pill";
    pillEl.innerText = "Loading snapshots...";
  }
  if (noticeEl) noticeEl.style.display = "none";

  let beforeOk = false;
  let afterOk = false;

  const tryDiff = () => {
    if (beforeOk && afterOk) {
      recomputeVisualPixelDiff();
    }
  };

  afterImg.onload = () => {
    afterOk = true;
    if (sideAfter) sideAfter.src = afterImg.src;
    tryDiff();
  };
  afterImg.onerror = () => {
    if (pillEl) {
      pillEl.className = "pill fail";
      pillEl.innerText = "Migrated snapshot unavailable";
    }
  };

  beforeImg.onload = () => {
    beforeOk = true;
    beforeImg.style.opacity = "1";
    if (sideBefore) sideBefore.src = beforeImg.src;
    if (noticeEl) noticeEl.style.display = "none";
    tryDiff();
  };
  beforeImg.onerror = () => {
    beforeOk = false;
    beforeImg.removeAttribute("src");
    beforeImg.style.opacity = "0";
    if (sideBefore) sideBefore.removeAttribute("src");
    if (pillEl) {
      pillEl.className = "pill warn";
      pillEl.innerText = isCustomRef ? "Invalid reference image" : "No SSRS source snapshot";
    }
    if (noticeEl) {
      noticeEl.style.display = "block";
      noticeEl.innerHTML = "SSRS live PNG snapshot is not available for this report (SSRS offline, parameterized, or local-folder mode). Upload a <strong>Reference PNG</strong> above to run the swipe slider and pixel-diff overlay.";
    }
  };

  afterImg.src = afterSrc;
  beforeImg.src = beforeSrc;
}

function recomputeVisualPixelDiff() {
  const beforeImg = document.getElementById("cmp-before-img");
  const afterImg = document.getElementById("cmp-after-img");
  const canvas = document.getElementById("cmp-diff-canvas");
  const pillEl = document.getElementById("cmp-similarity-pill");
  const statsEl = document.getElementById("cmp-diff-stats");
  if (!beforeImg || !afterImg || !canvas || !beforeImg.naturalWidth || !afterImg.naturalWidth) return;

  const w = Math.min(1280, Math.max(beforeImg.naturalWidth, afterImg.naturalWidth));
  const h = Math.round(afterImg.naturalHeight * (w / afterImg.naturalWidth));
  canvas.width = w;
  canvas.height = h;

  const offA = document.createElement("canvas");
  offA.width = w;
  offA.height = h;
  const ctxA = offA.getContext("2d");

  const offB = document.createElement("canvas");
  offB.width = w;
  offB.height = h;
  const ctxB = offB.getContext("2d");

  ctxA.fillStyle = "#ffffff";
  ctxA.fillRect(0, 0, w, h);
  ctxA.drawImage(beforeImg, 0, 0, w, Math.round(beforeImg.naturalHeight * (w / beforeImg.naturalWidth)));

  ctxB.fillStyle = "#ffffff";
  ctxB.fillRect(0, 0, w, h);
  ctxB.drawImage(afterImg, 0, 0, w, h);

  const dataA = ctxA.getImageData(0, 0, w, h).data;
  const dataB = ctxB.getImageData(0, 0, w, h).data;
  const outCtx = canvas.getContext("2d");
  const outImg = outCtx.createImageData(w, h);
  const out = outImg.data;

  const tol = Math.max(0, Math.min(50, parseInt(document.getElementById("cmp-tolerance")?.value || "8", 10) || 8));
  const totalPixels = w * h;
  let diffPixels = 0;

  for (let i = 0; i < dataA.length; i += 4) {
    const dr = Math.abs(dataA[i] - dataB[i]);
    const dg = Math.abs(dataA[i + 1] - dataB[i + 1]);
    const db = Math.abs(dataA[i + 2] - dataB[i + 2]);
    if (dr > tol || dg > tol || db > tol) {
      diffPixels++;
      out[i] = 255;
      out[i + 1] = 40;
      out[i + 2] = 110;
      out[i + 3] = 235;
    } else {
      const gray = Math.round(dataB[i] * 0.299 + dataB[i + 1] * 0.587 + dataB[i + 2] * 0.114);
      const faded = Math.min(255, Math.round(gray * 0.45 + 140));
      out[i] = faded;
      out[i + 1] = faded;
      out[i + 2] = faded;
      out[i + 3] = 255;
    }
  }

  outCtx.putImageData(outImg, 0, 0);
  const similarity = (((totalPixels - diffPixels) / totalPixels) * 100).toFixed(2);
  const simNum = parseFloat(similarity);

  if (pillEl) {
    pillEl.className = simNum >= 95 ? "pill ok" : simNum >= 85 ? "pill warn" : "pill fail";
    pillEl.innerText = `${similarity}% match (${diffPixels.toLocaleString()} diff px)`;
  }
  if (statsEl) {
    statsEl.innerText = `${similarity}% pixel match • ${diffPixels.toLocaleString()} of ${totalPixels.toLocaleString()} pixels differ (tolerance: ±${tol})`;
  }
}

