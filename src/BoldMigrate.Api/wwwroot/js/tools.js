// Bold Migrate — Standalone Tools, Settings & Landing Scanner

function toggleLfAuth() {
  const mode = document.getElementById("lf-auth-mode").value;
  document.getElementById("lf-creds-box").style.display = mode === "creds" ? "grid" : "none";
  document.getElementById("lf-token-box").style.display = mode === "token" ? "block" : "none";
}

// Fills the folder fields with the conventional SSRS export layout so the shape of the
// expected input is obvious. These are examples, not paths that exist on this machine.
// =============================================================================
// Settings & Power Utilities Modal (4-Tab Consolidation)
// =============================================================================
function openSettingsModal(tab = "tuning") {
  const overlay = document.getElementById("modal-settings-utilities");
  if (!overlay) return;
  overlay.style.display = "flex";
  setSettingsTab(tab);
}

function closeSettingsModal() {
  const overlay = document.getElementById("modal-settings-utilities");
  if (overlay) overlay.style.display = "none";
}

function setSettingsTab(tab = "tuning", btnEl = null) {
  const validTabs = ["tuning", "remap", "rules", "cleanup"];
  const activeTab = validTabs.includes(tab) ? tab : "tuning";

  validTabs.forEach(t => {
    const pane = document.getElementById(`stab-${t}`);
    if (pane) pane.style.display = t === activeTab ? "block" : "none";
  });

  const tabBar = document.getElementById("settings-modal-tabs");
  if (tabBar) {
    tabBar.querySelectorAll("button[data-stab]").forEach(btn => {
      btn.classList.toggle("active", btn === btnEl || btn.dataset.stab === activeTab);
    });
  }

  if (activeTab === "tuning" || activeTab === "rules") {
    if (typeof loadSettingsView === "function") loadSettingsView();
  } else if (activeTab === "cleanup") {
    if (typeof syncBulkDeleteFromActiveRun === "function") syncBulkDeleteFromActiveRun();
  }
}

document.addEventListener("keydown", (event) => {
  if (event.key !== "Escape") return;
  const topModalIds = [
    "modal-app-dialog",
    "modal-visual-compare",
    "modal-interactive-viewer",
    "modal-interactive-designer",
    "modal-rdl-diff",
    "modal-render-logs",
    "modal-snapshot-zoom",
    "modal-confirm-rescan",
    "modal-delete-reports",
  ];
  const hasTopOverlay = topModalIds.some(id => {
    const el = document.getElementById(id);
    return el && el.style.display && el.style.display !== "none";
  });
  if (hasTopOverlay) return;

  const settingsModal = document.getElementById("modal-settings-utilities");
  if (settingsModal && settingsModal.style.display && settingsModal.style.display !== "none") {
    closeSettingsModal();
    return;
  }
  if (typeof closeDataSourcesDrawer === "function") {
    closeDataSourcesDrawer();
  }
});

function prefillLocalFolderDemo() {
  const root = (document.getElementById("lf-report-folder").value || "").trim();
  const base = root ? root.replace(/[\\/]+(Reports)?[\\/]*$/i, "") : "C:\\SSRS_Export";

  document.getElementById("lf-report-folder").value = `${base}\\Reports`;
  document.getElementById("lf-ds-folder").value = `${base}\\Datasources`;
  document.getElementById("lf-dset-folder").value = `${base}\\Datasets`;
  document.getElementById("lf-backup-folder").value = `${base}\\Backup`;
  document.getElementById("lf-log-folder").value = `${base}\\logs`;
  showToast(`Filled the standard export layout under ${base}. Edit the paths to match your export.`);
}

async function executeLocalFolderMigration() {
  const request = {
    serverUrl: document.getElementById("lf-server-url").value,
    tenantId: document.getElementById("lf-tenant-id").value,
    username: document.getElementById("lf-auth-mode").value === "creds" ? document.getElementById("lf-user").value : null,
    password: document.getElementById("lf-auth-mode").value === "creds" ? document.getElementById("lf-pass").value : null,
    bearerToken: document.getElementById("lf-auth-mode").value === "token" ? document.getElementById("lf-token").value : null,
    reportFolder: document.getElementById("lf-report-folder").value,
    dataSourceFolder: document.getElementById("lf-ds-folder").value,
    dataSetFolder: document.getElementById("lf-dset-folder").value,
    backupFolder: document.getElementById("lf-backup-folder").value,
    logFolder: document.getElementById("lf-log-folder").value,
    targetDataSourceName: document.getElementById("lf-target-ds").value,
    exportStructuredLayout: document.getElementById("lf-export-layout").checked
  };

  const resultsCard = document.getElementById("lf-results-card");
  resultsCard.style.display = "block";
  document.getElementById("lf-status-summary").innerText = "Executing migration engine: scanning folders, resolving categories, and uploading...";

  const logBox = document.getElementById("lf-log-box");
  logBox.innerText = "Starting engine...\n";

  const logTimer = setInterval(async () => {
    const logs = await Api.getLocalFolderLogs();
    if (logs && logs.length > 0) {
      logBox.innerHTML = logs.map(l => {
        if (typeof l === 'string') return `<div class="log-line">${escapeHtml(l)}</div>`;
        const timeStr = l.timestamp ? new Date(l.timestamp).toLocaleTimeString() : "";
        return `<div class="log-line"><span class="log-ts">[${timeStr}]</span> ${escapeHtml(l.message || JSON.stringify(l))}</div>`;
      }).join("");
      logBox.scrollTop = logBox.scrollHeight;
    }
  }, 1000);

  await withButtonProgress("btn-start-lf", "Migrating Folders...", "Executing Local Folder Migration", async () => {
    try {
      const res = await Api.runLocalFolderMigration(request);
      clearInterval(logTimer);

      document.getElementById("lf-status-summary").innerText = res.message;
      document.getElementById("lf-rep-stat").innerText = `${res.migratedReports} / ${res.totalReports}`;
      document.getElementById("lf-ds-stat").innerText = `${res.migratedDataSources} / ${res.totalDataSources}`;
      document.getElementById("lf-dset-stat").innerText = `${res.migratedDataSets} / ${res.totalDataSets}`;

      if (res.logs && res.logs.length > 0) {
        logBox.innerHTML = res.logs.map(l => {
          if (typeof l === 'string') return `<div class="log-line">${escapeHtml(l)}</div>`;
          const timeStr = l.timestamp ? new Date(l.timestamp).toLocaleTimeString() : "";
          return `<div class="log-line"><span class="log-ts">[${timeStr}]</span> ${escapeHtml(l.message || JSON.stringify(l))}</div>`;
        }).join("");
        logBox.scrollTop = logBox.scrollHeight;
      }

      const linksDiv = document.getElementById("lf-download-links");
      linksDiv.innerHTML = `
        <span class="pill ok" style="padding:6px 12px;">Migration Completed</span>
        ${res.resultCsvPath ? `<span class="pill" style="background:var(--br-surface-hover);border:1px solid var(--br-border-strong);">CSV Summary: ${escapeHtml(res.resultCsvPath)}</span>` : ""}
        ${res.summaryMarkdownPath ? `<span class="pill" style="background:var(--br-surface-hover);border:1px solid var(--br-border-strong);">Markdown Summary: ${escapeHtml(res.summaryMarkdownPath)}</span>` : ""}
      `;

      showToast("Local folder migration completed successfully.");
    } catch (err) {
      clearInterval(logTimer);
      document.getElementById("lf-status-summary").innerText = `Migration failed: ${err.message}`;
      showToast(`Error: ${err.message}`);
    }
  }, "Scanning local .rdl/.rsds/.rsd folders and uploading to Bold Reports...");
}


async function loadSettingsView() {
  try {
    const s = await Api.getSettings();
    const set = (id, val) => {
      const el = document.getElementById(id);
      if (el && val !== undefined && val !== null) el.value = val;
    };
    // MigrationSettings serializes with dotted names (see JsonPropertyName attributes).
    set("setting-data-threshold", s["Validation.DataAccuracyThreshold"]);
    set("setting-extract-parallel", s["Extraction.MaxParallel"]);
    set("setting-upload-parallel", s["Upload.MaxParallel"]);
    set("setting-conflict-policy", s["Upload.ConflictPolicy"] || "Skip");
    set("setting-row-limit", s["Validation.SampleRowLimit"]);
    set("setting-shim-path", s["UrlShim.ListenPath"] || "/ReportServer");
    set("setting-preferred-api", s["Extraction.PreferredApi"] || "Auto");

    const delStaging = document.getElementById("setting-delete-staging");
    if (delStaging && s["Staging.DeleteOnClose"] !== undefined) delStaging.checked = !!s["Staging.DeleteOnClose"];

    const untrustedSql = document.getElementById("setting-allow-untrusted-sql");
    if (untrustedSql && s["Preview.AllowUntrustedSqlCert"] !== undefined) untrustedSql.checked = !!s["Preview.AllowUntrustedSqlCert"];

    const shim = document.getElementById("setting-shim-enabled");
    if (shim && s["UrlShim.Enabled"] !== undefined) shim.checked = !!s["UrlShim.Enabled"];

    const rulesTbody = document.getElementById("settings-rules-tbody");
    if (rulesTbody) {
      const rulesRes = await Api.getRules();
      const rules = rulesRes?.rules || rulesRes?.Rules || [];
      rulesTbody.innerHTML = rules.length > 0
        ? rules.map(r => `
            <tr>
              <td><code>${escapeHtml(r.id || r.Id)}</code></td>
              <td><span class="pill ${(r.severity || r.Severity) === "Blocked" ? "fail" : "warn"}">${escapeHtml(r.severity || r.Severity || "Info")}</span></td>
              <td><code>${escapeHtml(r.match || r.Match || r.element || "")}</code></td>
              <td style="font-size:12.5px;color:var(--br-text-secondary);">${escapeHtml(r.message || r.Message || "")}</td>
            </tr>`).join("")
        : `<tr><td colspan="4" style="color:var(--br-text-muted);padding:12px;">No compatibility rules found.</td></tr>`;
    }
  } catch (err) {
    showToast("Could not load settings: " + err.message);
  }
}

async function saveSettings() {
  const num = (id, fallback) => {
    const el = document.getElementById(id);
    const v = el ? parseFloat(el.value) : NaN;
    return Number.isFinite(v) ? v : fallback;
  };

  const payload = {
    "Validation.DataAccuracyThreshold": num("setting-data-threshold", 99.5),
    "Validation.SampleRowLimit": Math.round(num("setting-row-limit", 10000)),
    "Extraction.MaxParallel": Math.round(num("setting-extract-parallel", 4)),
    "Extraction.PreferredApi": document.getElementById("setting-preferred-api")?.value || "Auto",
    "Upload.MaxParallel": Math.round(num("setting-upload-parallel", 4)),
    "Upload.ConflictPolicy": document.getElementById("setting-conflict-policy")?.value || "Skip",
    "Staging.DeleteOnClose": document.getElementById("setting-delete-staging")?.checked ?? true,
    "Preview.AllowUntrustedSqlCert": document.getElementById("setting-allow-untrusted-sql")?.checked ?? false,
    "UrlShim.Enabled": document.getElementById("setting-shim-enabled")?.checked ?? false,
    "UrlShim.ListenPath": (document.getElementById("setting-shim-path")?.value || "/ReportServer").trim() || "/ReportServer"
  };

  await withButtonProgress("btn-save-settings", "Saving...", "Saving Migration Settings", async () => {
    try {
      await Api.updateSettings(payload);
      const trustCb = document.getElementById("conn-ds-trust-cert");
      if (trustCb) trustCb.checked = !!payload["Preview.AllowUntrustedSqlCert"];
      showToast("Settings saved. New thresholds apply to the next validation pass.");
      await loadSettingsView();
    } catch (err) {
      showToast("Save failed: " + err.message);
    }
  });
}

async function handleLandingFileScan(event) {
  const file = event.target.files[0];
  if (!file) return;

  const resultsDiv = document.getElementById("landing-scan-results");
  resultsDiv.style.display = "block";
  resultsDiv.innerHTML = `<div class="card" style="padding:16px;"><span class="bm-spin"></span><strong>Analyzing ${escapeHtml(file.name)}...</strong></div>`;
  beginActivity("landing-scan", "Scanning RDL File", `Analyzing ${file.name}...`, null);

  try {
    const formData = new FormData();
    formData.append("file", file);
    const data = await Api.scanFile(formData);

    let badgeClass = "pill ok";
    if (data.bucket === "NeedsReview") badgeClass = "pill warn";
    if (data.bucket === "Blocked") badgeClass = "pill fail";

    resultsDiv.innerHTML = `
      <div class="card" style="padding:16px;">
        <div style="display:flex;justify-content:space-between;align-items:center;">
          <div>
            <strong>${escapeHtml(data.fileName)}</strong>
            <span style="color:var(--br-text-muted);font-size:12px;margin-left:8px;">Schema: ${escapeHtml(data.schemaVersion)}</span>
          </div>
          <span class="${badgeClass}">${escapeHtml(data.bucket)}</span>
        </div>
        <p style="margin:8px 0 0;font-size:13px;color:var(--br-text-secondary);">${escapeHtml(data.message)}</p>
      </div>
    `;
  } catch (err) {
    resultsDiv.innerHTML = `<div class="card" style="padding:16px;color:var(--br-danger);">Scan failed: ${escapeHtml(err.message)}</div>`;
  } finally {
    endActivity("landing-scan");
  }
}

// -----------------------------------------------------------------------------
// Poller & Helpers
// -----------------------------------------------------------------------------
