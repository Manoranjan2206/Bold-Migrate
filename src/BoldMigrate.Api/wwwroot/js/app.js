// Bold Migrate — Enterprise Frontend Controller
// Realistic Sequential Migration Journey

let currentTab = "connections";
let currentRun = null;
let pollTimer = null;
let reportsCache = [];
let selectedReportIds = new Set();
let rdlCache = {};
let dataSourcesCache = [];
let expressionSamples = [];
let currentLibFilter = "all";
let isPaused = false;
let currentDiffMode = "side-by-side";
let selectedValidationReportId = null;
let latestValidation = null;

document.addEventListener("DOMContentLoaded", async () => {
  await initApp();
});

async function initApp() {
  try {
    // Load or create active migration run
    currentRun = await Api.getActiveRun();
    if (!currentRun) {
      currentRun = await Api.createOrGetRun("SSRS Live Migration", "http://localhost:8081/ReportServer");
    }

    // Load Expression samples
    expressionSamples = await Api.getExpressionSamples();
    if (expressionSamples && expressionSamples.length > 0) {
      loadSelectedSample(0);
    }

    // Initial polling and refresh
    await refreshConnectionsState();
    await refreshLibraryState();
    await refreshDashboardState();

    // Start background status poller
    startPoller();

    // Default to Step 1: Connections
    switchTab("connections");

    // If active catalog already has discovered items, update badges
    const poll = await Api.pollState(currentRun.id);
    if (poll && poll.reportsTotal > 0) {
      updateJourneyBadges(poll);
    }
  } catch (err) {
    console.error("Initialization error:", err);
  }
}

// -----------------------------------------------------------------------------
// View & Tab Switching
// -----------------------------------------------------------------------------
function switchTab(tabName) {
  currentTab = tabName;

  // Update sidebar .step
  document.querySelectorAll(".side-nav .step").forEach(el => el.classList.remove("on"));
  const navEl = document.getElementById(`nav-${tabName}`);
  if (navEl) navEl.classList.add("on");

  // Update journey steppers across tabs
  updateStepperProgression(tabName);

  // Update main panes
  document.querySelectorAll(".tab-pane").forEach(pane => pane.style.display = "none");
  const targetPane = document.getElementById(`tab-${tabName}`);
  if (targetPane) targetPane.style.display = "block";

  // Tab specific refreshes
  if (tabName === "connections") refreshConnectionsState();
  if (tabName === "library") refreshLibraryState();
  if (tabName === "dashboard") refreshDashboardState();
  if (tabName === "migration") refreshMigrationState();
  if (tabName === "validation") refreshValidationState();
  if (tabName === "cutover") refreshCutoverState();
  if (tabName === "settings") loadSettingsView();
}

function updateStepperProgression(activeTab) {
  const stepOrder = ["connections", "library", "dashboard", "migration", "validation", "cutover"];
  const activeIdx = stepOrder.indexOf(activeTab);

  document.querySelectorAll(".journey-stepper").forEach(stepper => {
    const steps = stepper.querySelectorAll(".journey-step");
    const dividers = stepper.querySelectorAll(".jstep-divider");

    steps.forEach((step, idx) => {
      step.classList.remove("active", "completed");
      const numSpan = step.querySelector(".jstep-num");
      if (idx < activeIdx) {
        step.classList.add("completed");
        if (numSpan) numSpan.innerText = "✓";
      } else if (idx === activeIdx) {
        step.classList.add("active");
        if (numSpan) numSpan.innerText = String(idx + 1);
      } else {
        if (numSpan) numSpan.innerText = String(idx + 1);
      }
    });

    dividers.forEach((div, idx) => {
      div.classList.toggle("completed", idx < activeIdx);
    });
  });
}

function showLandingView() {
  document.getElementById("app-shell").style.display = "none";
  document.getElementById("view-landing").style.display = "block";
  window.scrollTo(0, 0);
}

function showAppView(initialTab = "connections") {
  document.getElementById("view-landing").style.display = "none";
  document.getElementById("app-shell").style.display = "flex";
  switchTab(initialTab);
}

// -----------------------------------------------------------------------------
// Step 1: Connections & Discovery
// -----------------------------------------------------------------------------
async function refreshConnectionsState() {
  if (!currentRun) return;
  try {
    // Show the servers this run is ACTUALLY pointed at. Leaving the defaults in the
    // fields would let "Test connection" silently repoint the run at the wrong host.
    hydrateConnectionFields();

    const poll = await Api.pollState(currentRun.id);
    if (poll && poll.itemsTotal > 0) {
      const banner = document.getElementById("discovery-status-banner");
      if (banner) banner.style.display = "flex";

      const titleEl = document.getElementById("discovery-banner-title");
      if (titleEl) titleEl.innerText = `Active Estate Discovered (${poll.itemsTotal} items)`;

      const descEl = document.getElementById("discovery-banner-desc");
      if (descEl) {
        const plural = (n, one, many) => `${n} ${n === 1 ? one : many}`;
        descEl.innerText = `Discovered ${plural(poll.reportsTotal, "report", "reports")}, `
          + `${plural(poll.datasourcesCount, "data source", "data sources")}, `
          + `${plural(poll.datasetsCount, "dataset", "datasets")} across `
          + `${plural(poll.foldersCount, "folder", "folders")}.`;
      }
    }

    // Load real data sources into credentials vault
    dataSourcesCache = await Api.getDataSources(currentRun.id);
    renderDataSourcesTable();
  } catch (err) {
    console.warn("Connections refresh error:", err);
  }
}

function hydrateConnectionFields() {
  if (!currentRun) return;

  const urlEl = document.getElementById("conn-ssrs-url");
  if (urlEl && currentRun.sourceServerUrl) {
    urlEl.value = currentRun.sourceServerUrl;
  }

  const destEl = document.getElementById("conn-dest-url");
  if (destEl && currentRun.destinationServerUrl) {
    destEl.value = currentRun.destinationServerUrl;
  }

  const emailEl = document.getElementById("conn-dest-email");
  if (emailEl && currentRun.destinationUserEmail &&
      currentRun.destinationUserEmail !== "BearerTokenAuth") {
    emailEl.value = currentRun.destinationUserEmail;
  }

  // Reflect real connection state instead of the optimistic markup defaults.
  const ssrsConnected = !!currentRun.sourceServerUrl &&
    ["Connected", "Fetching", "Analysed", "Uploading", "Uploaded", "Validating", "Validated", "CutOver"]
      .includes(currentRun.status);
  setConnectionIndicator("conn-ssrs-status-pill", "side-ssrs-dot", "side-ssrs-text",
    ssrsConnected, ssrsConnected ? `Connected · ${currentRun.sourceVersion || ""}`.trim() : "Not connected");

  const destConnected = !!currentRun.destinationServerUrl;
  setConnectionIndicator("conn-dest-status-pill", "side-dest-dot", "side-dest-text",
    destConnected, destConnected ? "Connected" : "Not connected");
}

function setConnectionIndicator(pillId, dotId, textId, ok, label) {
  const pill = document.getElementById(pillId);
  if (pill) {
    pill.className = ok ? "pill ok" : "pill";
    pill.innerText = label;
  }
  const text = document.getElementById(textId);
  if (text) text.innerText = ok ? "Connected" : "Not connected";
  const dot = document.getElementById(dotId);
  if (dot) dot.style.background = ok ? "#10B981" : "#94A3B8";
}

function renderDataSourcesTable() {
  const tbody = document.getElementById("conn-datasources-table");
  if (!tbody) return;

  if (!dataSourcesCache || dataSourcesCache.length === 0) {
    tbody.innerHTML = `
      <tr>
        <td colspan="6" style="text-align:center;color:#64748B;padding:20px;">
          No data sources discovered yet. Click "Save & Discover Catalog" to scan SSRS.
        </td>
      </tr>
    `;
    return;
  }

  tbody.innerHTML = dataSourcesCache.map(ds => `
    <tr>
      <td><strong>${escapeHtml(ds.name)}</strong></td>
      <td><code>${escapeHtml(ds.sourcePath || ds.parentPath || "/")}</code></td>
      <td><span class="pill" style="font-size:11px;">${escapeHtml(ds.itemType || "DataSource")}</span></td>
      <td>
        <input id="ds-user-${ds.id}" data-ds-id="${ds.id}" class="ds-cred-user" placeholder="Not set"
               value="${escapeHtml(ds.userName || "")}" aria-label="User for ${escapeHtml(ds.name)}"
               style="width:130px;padding:4px 8px;border:1px solid #CBD5E1;border-radius:4px;">
      </td>
      <td>
        <input type="password" id="ds-pass-${ds.id}" data-ds-id="${ds.id}" class="ds-cred-pass"
               placeholder="${ds.hasCredentials ? "Stored (unchanged)" : "Not set"}" aria-label="Password for ${escapeHtml(ds.name)}"
               autocomplete="new-password"
               style="width:130px;padding:4px 8px;border:1px solid #CBD5E1;border-radius:4px;">
      </td>
      <td style="white-space:nowrap;">
        <span class="pill ${ds.hasCredentials ? "ok" : ""}" id="ds-status-${ds.id}">${ds.hasCredentials ? "Stored" : "Credentials not set"}</span>
        <button class="btn2 btn-sm" style="margin-left:6px;" onclick="saveDataSourceCredentials('${ds.id}')">Save</button>
      </td>
    </tr>
  `).join("");
}

// Credentials stay in server memory for this session. The local Step 5 viewer/designer use
// them; data sources without credentials prompt for them when a report runs.
async function saveDataSourceCredentials(dsId) {
  if (!currentRun) return;
  const user = document.getElementById(`ds-user-${dsId}`)?.value.trim() || "";
  const pass = document.getElementById(`ds-pass-${dsId}`)?.value || "";
  const pill = document.getElementById(`ds-status-${dsId}`);
  const ds = dataSourcesCache.find(d => d.id === dsId);

  // An empty password with an unchanged, already-stored user keeps the stored password.
  if (user && !pass && ds?.hasCredentials && ds.userName === user) {
    showToast("Credentials unchanged.");
    return;
  }

  try {
    const res = await Api.setDataSourceCredentials(currentRun.id, dsId, user, pass);
    if (ds) {
      ds.hasCredentials = res.hasCredentials;
      ds.userName = res.hasCredentials ? user : null;
    }
    const passEl = document.getElementById(`ds-pass-${dsId}`);
    if (passEl) {
      passEl.value = "";
      passEl.placeholder = res.hasCredentials ? "Stored (unchanged)" : "Not set";
    }
    if (pill) {
      pill.className = res.hasCredentials ? "pill ok" : "pill";
      pill.innerText = res.hasCredentials ? "Stored" : "Credentials not set";
    }
    showToast(res.hasCredentials ? "Data source credentials saved for this session." : "Data source credentials cleared.");
  } catch (err) {
    showToast(err.message);
  }
}

function toggleSsrsAuthFields() {
  const mode = document.getElementById("conn-ssrs-auth").value;
  const isExplicit = mode === "WindowsExplicit" || mode === "Basic";
  const credsRow = document.getElementById("conn-ssrs-creds-row");
  const noteElem = document.getElementById("conn-ssrs-integrated-note");
  if (credsRow) credsRow.style.display = isExplicit ? "grid" : "none";
  if (noteElem) noteElem.style.display = isExplicit ? "none" : "block";
}

function toggleDestAuthFields() {
  const mode = document.getElementById("conn-dest-auth-mode").value;
  document.getElementById("conn-dest-creds-row").style.display = mode === "credentials" ? "grid" : "none";
  document.getElementById("conn-dest-token-row").style.display = mode === "token" ? "block" : "none";
}

async function testSsrsConnection() {
  if (!currentRun) return;
  const url = document.getElementById("conn-ssrs-url").value;
  const mode = document.getElementById("conn-ssrs-auth").value;
  let user = "";
  let pass = "";
  if (mode !== "Integrated") {
    user = document.getElementById("conn-ssrs-user").value;
    pass = document.getElementById("conn-ssrs-pass").value;
  }

  const pill = document.getElementById("conn-ssrs-status-pill");
  pill.className = "pill run";
  pill.innerText = "Probing...";

  const res = await Api.testConnection(currentRun.id, url, user, pass, mode);
  if (res.success) {
    pill.className = "pill ok";
    pill.innerText = `Connected · ${res.version || "REST v2.0"}`;
    showToast("SSRS Connection verified successfully.");
  } else {
    pill.className = "pill fail";
    pill.innerText = "Connection failed";
    showToast(`SSRS connection failed: ${res.message || "Unreachable"}`);
  }
}

async function testDestConnection() {
  if (!currentRun) return;
  const url = document.getElementById("conn-dest-url").value;
  const mode = document.getElementById("conn-dest-auth-mode").value;
  const email = document.getElementById("conn-dest-email").value;
  const pass = document.getElementById("conn-dest-pass").value;
  const token = document.getElementById("conn-dest-token").value;

  const pill = document.getElementById("conn-dest-status-pill");
  pill.className = "pill run";
  pill.innerText = "Connecting...";

  const res = await Api.testDestinationConnection(
    currentRun.id,
    url,
    mode === "credentials" ? email : null,
    mode === "credentials" ? pass : null,
    mode === "token" ? token : null
  );

  if (res.success) {
    pill.className = "pill ok";
    pill.innerText = "Token valid";
    showToast("Bold Reports connection verified. Ready for upload.");
  } else {
    pill.className = "pill fail";
    pill.innerText = "Failed";
    showToast(`Bold Reports authentication failed: ${res.message}`);
  }
}

async function saveAndDiscoverCatalog(forceRescan = false) {
  if (!currentRun) return;
  const btn = document.getElementById("btn-discover-primary");
  if (btn) {
    btn.disabled = true;
    btn.innerText = "Discovering SSRS Catalog...";
  }

  showToast("Scanning SSRS catalog: folders, data sources, datasets, and reports...");

  try {
    // 1. Trigger Fetch
    await Api.fetchEstate(currentRun.id);

    // 2. Poll until items are cataloged (wait up to 5 seconds)
    let discovered = false;
    for (let i = 0; i < 10; i++) {
      await new Promise(r => setTimeout(r, 600));
      const poll = await Api.pollState(currentRun.id);
      if (poll && poll.reportsTotal > 0) {
        discovered = true;
        break;
      }
    }

    // 3. Trigger Assessment check
    await Api.checkEstate(currentRun.id);

    showToast("Catalog discovery complete! Proceeding to Report Library...");

    // 4. Refresh caches and transition to Step 2: Report Library
    await refreshConnectionsState();
    await refreshLibraryState();
    switchTab("library");
  } catch (err) {
    showToast("Discovery error: " + err.message);
  } finally {
    if (btn) {
      btn.disabled = false;
      btn.innerText = "Save & Discover Catalog →";
    }
  }
}

// -----------------------------------------------------------------------------
// Step 2: Report Library (with Multi-Select & Direct Migration Trigger)
// -----------------------------------------------------------------------------
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
        <td colspan="8" style="text-align:center;color:#64748B;padding:32px;">
          No reports found. Connect your SSRS server in Step 1 to discover catalog items.
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
    if (currentLibFilter === "clean") return r.bucket === "Clean";
    if (currentLibFilter === "autofix") return r.bucket === "AutoFixed";
    if (currentLibFilter === "review") return r.bucket === "NeedsReview";
    if (currentLibFilter === "blocked") return r.bucket === "Blocked";
    return true;
  });

  // Update counts
  const cleanCount = reportsCache.filter(r => r.bucket === "Clean").length;
  const autofixCount = reportsCache.filter(r => r.bucket === "AutoFixed").length;
  const reviewCount = reportsCache.filter(r => r.bucket === "NeedsReview").length;
  const blockedCount = reportsCache.filter(r => r.bucket === "Blocked").length;

  document.getElementById("lib-count-all").innerText = reportsCache.length;
  document.getElementById("lib-count-clean").innerText = cleanCount;
  document.getElementById("lib-count-autofix").innerText = autofixCount;
  document.getElementById("lib-count-review").innerText = reviewCount;
  document.getElementById("lib-count-blocked").innerText = blockedCount;

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
    let bucketClass = "pill ok";
    if (r.bucket === "AutoFixed") bucketClass = "pill ok";
    if (r.bucket === "NeedsReview") bucketClass = "pill warn";
    if (r.bucket === "Blocked") bucketClass = "pill fail";

    const isSelected = selectedReportIds.has(r.id);
    const sizeFormatted = r.sizeBytes > 1024 ? `${(r.sizeBytes / 1024).toFixed(1)} KB` : `${r.sizeBytes} B`;
    const blockReasonText = r.blockReason ? escapeHtml(r.blockReason) : (r.bucket === "Clean" ? "100% Native RDL" : "Auto-inlined");

    return `
      <tr>
        <td class="check-cell">
          <input type="checkbox" class="report-checkbox" data-id="${r.id}" ${isSelected ? "checked" : ""} onchange="handleReportCheckboxChange('${r.id}', this.checked)">
        </td>
        <td><strong>${escapeHtml(r.name)}</strong></td>
        <td><code>${escapeHtml(r.folder)}</code></td>
        <td style="color:#64748B;">${sizeFormatted}</td>
        <td>${r.parameterCount || 0}</td>
        <td><span class="${bucketClass}">${escapeHtml(r.bucket)}</span></td>
        <td style="font-size:12px;color:#475569;">${blockReasonText}</td>
        <td style="text-align:right;white-space:nowrap;">
          <button class="btn2 btn-sm" onclick="selectReportForValidation('${r.id}')">Inspect & Validate</button>
          <button class="btn2 btn-danger btn-sm" onclick="deleteSingleBoldReport('${r.id}', '${escapeHtml(r.name).replace(/'/g, "\\'")}')" title="Delete this report from Bold Reports destination" style="margin-left:4px;padding:4px 8px;">
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

function selectAllCleanAndAutoFix() {
  selectedReportIds.clear();
  reportsCache.forEach(r => {
    if (r.bucket === "Clean" || r.bucket === "AutoFixed") {
      selectedReportIds.add(r.id);
    }
  });
  renderReportLibrary();
  showToast(`Selected ${selectedReportIds.size} Clean & Auto-Fixed reports.`);
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
        <span class="pill" style="font-size:11px;background:#E2E8F0;color:#334155;">
          ${s.totalReports} report${s.totalReports === 1 ? '' : 's'}
        </span>
      </label>
    `).join("");
  } catch (err) {
    listEl.innerHTML = `<div style="color:#DC2626;font-size:12px;padding:6px;">Failed to load sections: ${err.message}</div>`;
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
      alert("Please select at least one section to delete.");
      return;
    }
    options = { sections: checkedSections, deleteCategories };
  } else if (isSelected) {
    if (selectedReportIds.size === 0) {
      alert("No reports are currently selected in the Report Library.");
      return;
    }
    options = { reportIds: Array.from(selectedReportIds) };
  }

  // Confirmation prompt
  const scopeMsg = isAll ? `ALL ${reportsCache.length} reports` :
                   isSection ? `${options.sections.length} section(s)` :
                   `${selectedReportIds.size} selected report(s)`;

  if (!confirm(`Are you sure you want to delete ${scopeMsg} from Bold Reports Server?\n\nThis will remove them from the destination server and reset their local migration state to Analysed.`)) {
    return;
  }

  // Update button state
  if (btnConfirm) btnConfirm.disabled = true;
  if (btnCancel) btnCancel.disabled = true;
  if (confirmText) confirmText.innerText = "Deleting from Bold Reports...";
  if (statusEl) {
    statusEl.style.display = "block";
    statusEl.style.background = "#EFF6FF";
    statusEl.style.color = "#1E40AF";
    statusEl.style.border = "1px solid #BFDBFE";
    statusEl.innerHTML = `<strong>Processing:</strong> Deleting ${scopeMsg} from Bold Reports destination...`;
  }

  try {
    const res = await Api.deleteDestinationReports(currentRun.id, options);
    if (res.success) {
      if (statusEl) {
        statusEl.style.background = "#ECFDF5";
        statusEl.style.color = "#065F46";
        statusEl.style.border = "1px solid #A7F3D0";
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
      statusEl.style.background = "#FEF2F2";
      statusEl.style.color = "#991B1B";
      statusEl.style.border = "1px solid #FECACA";
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
  if (!confirm(`Delete report '${reportName}' from Bold Reports destination?\n\nThis will remove it from the Bold Reports server and reset its status to Analysed.`)) {
    return;
  }

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

async function startMigrationWithSelected() {
  if (!currentRun) return;
  if (selectedReportIds.size === 0) {
    showToast("Please select at least one report to migrate.");
    return;
  }

  showToast(`Saving selection of ${selectedReportIds.size} reports and initiating upload pipeline...`);

  try {
    await Api.selectReports(currentRun.id, Array.from(selectedReportIds));
    switchTab("migration");
    await startBatchUpload();
  } catch (err) {
    showToast("Migration start error: " + err.message);
  }
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
  showToast("Analyzing RDL schemas, expressions, datasources, and assemblies...");
  await Api.checkEstate(currentRun.id);
  await refreshDashboardState();
  switchTab("dashboard");
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
    const readinessPct = total > 0 ? Math.round(((clean + autofix) / total) * 100) : 0;
    document.getElementById("dash-tile-readiness").innerText = `${readinessPct}%`;
    document.getElementById("dash-tile-review").innerText = review;
    document.getElementById("dash-tile-blocked").innerText = blocked;

    // Real compatibility breakdown percentages
    const cleanPct = total > 0 ? Math.round((clean / total) * 100) : 0;
    const autofixPct = total > 0 ? Math.round((autofix / total) * 100) : 0;
    const reviewPct = total > 0 ? Math.round((review / total) * 100) : 0;
    const blockedPct = total > 0 ? Math.round((blocked / total) * 100) : 0;

    setBarAndPct("comp-clean", cleanPct);
    setBarAndPct("comp-autofix", autofixPct);
    setBarAndPct("comp-review", reviewPct);
    setBarAndPct("comp-blocked", blockedPct);

    // Footer must agree with the tile above it.
    const footerReadiness = document.getElementById("dash-footer-readiness");
    if (footerReadiness) {
      footerReadiness.innerHTML = total > 0
        ? `Readiness score: <strong>${readinessPct}%</strong> — ${clean + autofix} of ${total} report(s) ready for direct import.`
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

  const flagged = reportsCache.filter(r => r.bucket === "Blocked" || r.bucket === "NeedsReview");
  if (flagged.length === 0) {
    container.innerHTML = `
      <div style="padding:16px;text-align:center;color:#059669;font-weight:600;">
        ✓ All reports are 100% clean and ready for direct import!
      </div>
    `;
    return;
  }

  container.innerHTML = flagged.map(r => {
    const isBlocked = r.bucket === "Blocked";
    const pillClass = isBlocked ? "pill fail" : "pill warn";
    let desc = r.blockReason || (isBlocked ? "Custom assembly reference detected" : "Requires design review");

    return `
      <div class="flagged-item">
        <div>
          <strong>${escapeHtml(r.name)}</strong>
          <span style="color:#64748B;font-size:11.5px;margin-left:6px;">${escapeHtml(r.folder)}</span>
          <div style="font-size:12px;color:#475569;margin-top:2px;">${escapeHtml(desc)}</div>
        </div>
        <div style="display:flex;align-items:center;gap:8px;">
          <span class="${pillClass}">${escapeHtml(r.bucket)}</span>
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
  showToast("Re-evaluating compatibility rules...");
  await Api.checkEstate(currentRun.id);
  await refreshDashboardState();
  showToast("Assessment updated.");
}

// -----------------------------------------------------------------------------
// Step 4: Migration Pipeline
// -----------------------------------------------------------------------------
async function refreshMigrationState(knownPoll) {
  if (!currentRun) return;
  try {
    const poll = knownPoll || await Api.pollState(currentRun.id);
    if (poll) {
      // Discovered counts only — a zero means nothing of that type was found,
      // which must not be masked by a placeholder number.
      const folders = poll.foldersCount || 0;
      const datasources = poll.datasourcesCount || 0;
      const datasets = poll.datasetsCount || 0;
      const repsTotal = poll.reportsTotal || 0;
      const repsUploaded = poll.reportsUploaded || 0;

      document.getElementById("stage-folders-count").innerText = folders;
      document.getElementById("stage-ds-count").innerText = datasources;
      document.getElementById("stage-dset-count").innerText = datasets;
      document.getElementById("stage-rep-count").innerText = `${repsUploaded} / ${repsTotal}`;

      // Progress tracks reports, the only stage whose completion is actually recorded.
      const progressPct = repsTotal > 0 ? Math.min(100, Math.round((repsUploaded / repsTotal) * 100)) : 0;

      const footerInfo = document.getElementById("migration-footer-info");
      if (footerInfo) {
        const itemsTotal = poll.itemsTotal || 0;
        footerInfo.innerText = itemsTotal > 0
          ? `${repsUploaded} of ${repsTotal} reports uploaded (${itemsTotal} catalog items discovered).`
          : "Nothing discovered yet. Run Step 1: Connect & Discover first.";
      }

      // Mark the non-report stages honestly: they are only "done" if there was
      // something to upload and it completed.
      setStagePill("stage-folders-pill", folders, poll.status);
      setStagePill("stage-ds-pill", datasources, poll.status);
      setStagePill("stage-dset-pill", datasets, poll.status);

      const bar = document.getElementById("migration-progress-bar");
      const pctSpan = document.getElementById("migration-progress-pct");
      if (bar) bar.style.width = `${progressPct}%`;
      if (pctSpan) pctSpan.innerText = `${progressPct}%`;

      const repPill = document.getElementById("stage-rep-pill");
      if (repPill) {
        if (poll.status === "Uploading") {
          repPill.className = "pill run";
          repPill.innerText = "Uploading";
        } else if (repsTotal > 0 && repsUploaded === repsTotal) {
          repPill.className = "pill ok";
          repPill.innerText = "Completed";
        } else if (repsUploaded > 0) {
          repPill.className = "pill warn";
          repPill.innerText = `${repsUploaded} of ${repsTotal}`;
        } else {
          repPill.className = "pill";
          repPill.innerText = repsTotal > 0 ? "Ready" : "Nothing discovered";
        }
      }
    }

    const logs = await Api.getLiveLog(currentRun.id);
    if (logs && logs.length > 0) {
      const logBox = document.getElementById("migration-live-log-box");
      logBox.innerHTML = logs.map(l => {
        if (typeof l === 'string') return `<div style="font-family:Consolas, monospace;font-size:11.5px;color:#CBD5E1;line-height:1.5;">${escapeHtml(l)}</div>`;
        const timeStr = l.timestamp ? new Date(l.timestamp).toLocaleTimeString() : "";
        const lvl = (l.level || "INFO").toUpperCase();
        let lvlColor = "#38BDF8";
        if (lvl.includes("ERR")) lvlColor = "#F87171";
        else if (lvl.includes("WARN")) lvlColor = "#FBBF24";
        else if (lvl.includes("INFO")) lvlColor = "#34D399";
        const code = l.eventCode ? `[${escapeHtml(l.eventCode)}] ` : "";
        const msg = l.message || "";
        return `<div style="line-height:1.5;margin-bottom:2px;font-family:Consolas, monospace;font-size:11.5px;color:#CBD5E1;">
          <span style="color:#64748B;">[${timeStr}]</span> 
          <span style="color:${lvlColor};font-weight:600;">[${escapeHtml(lvl)}]</span> 
          <span style="color:#94A3B8;">${code}</span>
          <span style="color:#F1F5F9;">${escapeHtml(msg)}</span>
        </div>`;
      }).join("");
      logBox.scrollTop = logBox.scrollHeight;
    }
  } catch { }
}

async function startBatchUpload() {
  if (!currentRun) return;
  const btn = document.getElementById("btn-start-migration-tab");
  btn.disabled = true;
  btn.innerText = "Uploading...";

  showToast("Starting 4-stage dependency upload to Bold Reports...");
  try {
    await Api.startUpload(currentRun.id);
    showToast("Upload pipeline actively executing.");
  } catch (err) {
    showToast("Upload error: " + err.message);
    btn.disabled = false;
    btn.innerText = "Start Upload Pipeline";
  }
}

async function togglePauseMigration() {
  if (!currentRun) return;
  const btn = document.getElementById("btn-pause-migration");
  if (!isPaused) {
    await Api.startUpload(currentRun.id, "pause");
    isPaused = true;
    btn.innerText = "Resume";
    showToast("Upload execution paused.");
  } else {
    await Api.startUpload(currentRun.id, "resume");
    isPaused = false;
    btn.innerText = "Pause";
    showToast("Upload execution resumed.");
  }
}

// A stage with nothing to upload is "None", not "Ready" or "Completed".
function setStagePill(elId, count, status) {
  const el = document.getElementById(elId);
  if (!el) return;
  if (count === 0) {
    el.className = "pill";
    el.innerText = "None discovered";
  } else if (status === "Uploading") {
    el.className = "pill run";
    el.innerText = "Uploading";
  } else if (status === "Uploaded" || status === "Validated" || status === "CutOver") {
    el.className = "pill ok";
    el.innerText = "Completed";
  } else {
    el.className = "pill";
    el.innerText = "Ready";
  }
}

function clearLiveLogViewer() {
  const box = document.getElementById("migration-live-log-box");
  if (box) box.innerHTML = "<div style='color:#64748B;font-family:Consolas, monospace;font-size:12px;'>Log cleared.</div>";
}

// -----------------------------------------------------------------------------
// Step 5: Validation & Diff
// -----------------------------------------------------------------------------
function selectReportForValidation(reportId) {
  selectedValidationReportId = reportId;
  switchTab("validation");
}

async function refreshValidationState() {
  if (!reportsCache || reportsCache.length === 0) {
    await refreshLibraryState();
  }

  const selector = document.getElementById("val-report-selector");
  if (!selector) return;

  // Populate selector with real reports
  selector.innerHTML = reportsCache.map(r => `
    <option value="${r.id}" ${r.id === selectedValidationReportId ? "selected" : ""}>
      ${escapeHtml(r.name)} (${escapeHtml(r.folder)})
    </option>
  `).join("");

  if (!selectedValidationReportId && reportsCache.length > 0) {
    selectedValidationReportId = reportsCache[0].id;
  }

  await loadReportValidation(selectedValidationReportId);
}

async function runValidationPass() {
  if (!currentRun) return;
  const btn = document.getElementById("btn-run-validation");
  if (btn) { btn.disabled = true; btn.innerText = "Validating..."; }

  showToast("Rendering each report on SSRS and Bold Reports and comparing output...");
  try {
    await Api.startValidation(currentRun.id);

    // Wait for the backend pass to produce results for the selected report.
    for (let i = 0; i < 20; i++) {
      await new Promise(r => setTimeout(r, 1000));
      const v = await Api.getReportValidation(currentRun.id, selectedValidationReportId);
      if (v && v.validated) break;
    }

    await refreshLibraryState();
    await loadReportValidation(selectedValidationReportId);
    showToast("Validation pass complete.");
  } catch (err) {
    showToast("Validation failed: " + err.message);
  } finally {
    if (btn) { btn.disabled = false; btn.innerText = "Run Validation"; }
  }
}

async function loadReportValidation(reportId) {
  selectedValidationReportId = reportId;
  const report = reportsCache.find(r => r.id === reportId) || reportsCache[0];
  if (!report) return;

  document.getElementById("val-report-title").innerText = `Step 5: Output Validation — ${report.name}`;

  // Update metadata bar
  document.getElementById("val-meta-folder").innerText = report.folder;
  const sizeFormatted = report.sizeBytes > 1024 ? `${(report.sizeBytes / 1024).toFixed(1)} KB` : `${report.sizeBytes} B`;
  document.getElementById("val-meta-size").innerText = sizeFormatted;
  document.getElementById("val-meta-params").innerText = report.parameterCount || 0;

  const bucketPill = document.getElementById("val-meta-bucket");
  bucketPill.innerText = report.bucket;
  bucketPill.className = report.bucket === "Clean" || report.bucket === "AutoFixed" ? "pill ok" : (report.bucket === "NeedsReview" ? "pill warn" : "pill fail");

  const blockerSpan = document.getElementById("val-meta-blocker");
  if (report.blockReason) {
    blockerSpan.style.display = "inline";
    blockerSpan.innerText = `Blocker: ${report.blockReason}`;
  } else {
    blockerSpan.style.display = "none";
  }

  // Measured results only — never an assumed score.
  let validation = null;
  try {
    validation = await Api.getReportValidation(currentRun.id, report.id);
  } catch {
    validation = null;
  }
  latestValidation = validation;

  renderValidationMetrics(validation);
  renderValidationViewport(report, validation);
}

function fmtPct(v) {
  if (v === null || v === undefined) return "—";
  return `${Number(v).toFixed(1)}%`;
}

function renderValidationMetrics(v) {
  const accEl = document.getElementById("val-data-acc");
  const simEl = document.getElementById("val-vis-sim");
  const rowsEl = document.getElementById("val-rows-matched");
  const stateEl = document.getElementById("val-state");

  if (stateEl) {
    const st = (v && v.state) || "Unknown";
    const pretty = st.replace(/([a-z])([A-Z])/g, "$1 $2");
    stateEl.innerText = (v && v.validated) ? pretty : "Not validated";
    stateEl.style.color = st === "AutoPassed" ? "#059669"
      : (st === "AwaitingReview" ? "#D97706"
      : (st.includes("Failed") ? "#DC2626" : "#64748B"));
  }

  if (!v || !v.validated) {
    // No measurement exists. Show that plainly rather than inventing numbers.
    [accEl, simEl].forEach(el => { if (el) { el.innerText = "—"; el.style.color = "#94A3B8"; } });
    if (rowsEl) { rowsEl.innerText = "—"; rowsEl.style.color = "#94A3B8"; }
    return;
  }

  if (accEl) {
    accEl.innerText = fmtPct(v.dataAccuracy);
    accEl.style.color = v.dataAccuracy >= 99 ? "#059669" : (v.dataAccuracy >= 90 ? "#D97706" : "#DC2626");
  }
  // The second tile reports pagination, which is actually measured. Pixel-level comparison
  // is not implemented, so no similarity percentage is shown.
  if (simEl) {
    const sp = v.sourcePageCount, dp = v.destinationPageCount;
    if (sp === null || sp === undefined || dp === null || dp === undefined) {
      simEl.innerText = "n/a";
      simEl.style.color = "#94A3B8";
      simEl.title = v.renderSummary || "A PDF render was not available from both servers.";
    } else {
      simEl.innerText = `${dp} / ${sp}`;
      simEl.style.color = sp === dp ? "#2563EB" : "#DC2626";
      simEl.title = v.renderSummary || "";
    }
  }
  if (rowsEl) {
    rowsEl.innerText = `${v.rowsDestination ?? 0} / ${v.rowsSource ?? 0}`;
    rowsEl.style.color = (v.rowsDestination === v.rowsSource) ? "#0F172A" : "#DC2626";
  }
}

function setDiffView(mode) {
  currentDiffMode = mode;
  document.querySelectorAll(".seg button").forEach(b => b.classList.remove("sel"));
  if (mode === "side-by-side") document.getElementById("seg-side-by-side")?.classList.add("sel");
  if (mode === "rdl") document.getElementById("seg-rdl-diff")?.classList.add("sel");
  if (mode === "designer") document.getElementById("seg-designer")?.classList.add("sel");
  if (mode === "pixel") document.getElementById("seg-pixel-diff")?.classList.add("sel");
  if (mode === "data") document.getElementById("seg-data-diff")?.classList.add("sel");
  if (mode === "staged") document.getElementById("seg-staged")?.classList.add("sel");

  const report = reportsCache.find(r => r.id === selectedValidationReportId) || reportsCache[0];
  renderValidationViewport(report);
}

function renderValidationViewport(report, v) {
  const grid = document.getElementById("val-viewport-grid");
  if (!grid) return;

  // Leaving the staged list tears down any open local viewer/designer.
  if (currentDiffMode !== "staged") {
    Preview.destroyWidgets();
    Preview.open = null;
  }

  if (currentDiffMode === "staged") {
    if (currentRun) Preview.render(grid);
    return;
  }

  if (v === undefined) v = latestValidation;
  const name = report ? escapeHtml(report.name) : "Report";

  if (currentDiffMode === "rdl") {
    grid.innerHTML = `
      <div class="card" style="grid-column: 1 / -1; padding: 18px;">
        <div style="display:flex;justify-content:space-between;align-items:center;margin-bottom:12px;">
          <h2>RDL Definition Code Diff: ${name}</h2>
          <span class="pill ok">Schema 2016 / RDL 2010</span>
        </div>
        <div class="rdl-diff-container" id="rdl-diff-box">
          <div class="rdl-pane">
            <div class="rdl-pane-title">
              <span>Source SSRS Definition (Original .rdl)</span>
              <small>External Shared Datasets &amp; DataSources</small>
            </div>
            <pre class="rdl-code" id="rdl-code-original">Loading RDL source definition...</pre>
          </div>
          <div class="rdl-pane">
            <div class="rdl-pane-title">
              <span>Target Bold Reports Definition (Transformed .rdl)</span>
              <small>Inlined Datasets &amp; Rewritten DS References</small>
            </div>
            <pre class="rdl-code" id="rdl-code-transformed">Loading transformed RDL definition...</pre>
          </div>
        </div>
      </div>
    `;
    loadReportRdlDiff(report);
    return;
  }

  if (currentDiffMode === "designer") {
    let categoryName = "Sample Reports";
    if (report.folder && report.folder !== "/") {
      categoryName = report.folder.replace(/^\/+|\/+$/g, '');
    }
    const reportPath = `/${categoryName}/${name}`;

    grid.innerHTML = `
      <div class="card" style="grid-column: 1 / -1; padding: 0; overflow: hidden; height: 800px; display: flex; flex-direction: column;">
        <div style="background: #0F172A; color: #fff; padding: 10px 16px; display: flex; justify-content: space-between; align-items: center; border-bottom: 1px solid #334155;">
          <div style="display:flex; align-items:center; gap:12px;">
            <div style="font-size:15px; font-weight:700; color:#38BDF8; display:flex; align-items:center; gap:6px;">
              <span>⚡ Bold Reports Web Designer</span>
            </div>
            <span class="pill ok" style="font-size:11.5px;">Report: ${escapeHtml(name)}</span>
            <span style="font-size:11.5px; color:#94A3B8;">Path: <code>${escapeHtml(reportPath)}</code></span>
          </div>
          <div style="display:flex; gap:10px; align-items:center;">
            <button class="btn2 btn-sm" onclick="setDiffView('rdl')">View RDL Diff</button>
            <button class="btn2 btn-sm" onclick="setDiffView('side-by-side')">Exit Designer</button>
          </div>
        </div>
        <div id="bold-designer-loading-overlay" style="padding:40px; text-align:center; color:#64748B; background:#F8FAFC; flex:1; display:flex; flex-direction:column; justify-content:center; align-items:center;">
          <div style="font-size:17px; font-weight:600; color:#0F172A; margin-bottom:8px;">Initializing Bold Reports Web Designer...</div>
          <div style="font-size:13px; color:#64748B;">Connecting to Report Server at <code>http://localhost:8090/reporting/reportservice/api/Designer</code></div>
        </div>
        <div id="bold-report-designer" style="flex:1; width:100%; height:100%; min-height:720px; display:none;"></div>
      </div>
    `;

    initBoldReportDesigner(report, reportPath);
    return;
  }

  if (currentDiffMode === "pixel") {
    if (!v || !v.validated) {
      grid.innerHTML = notValidatedPanel("Visual comparison");
      return;
    }
    const sp = v.sourcePageCount, dp = v.destinationPageCount;
    const measured = sp != null && dp != null;
    grid.innerHTML = `
      <div class="card" style="grid-column: 1 / -1; padding: 20px;">
        <h2>Layout Comparison</h2>
        <p class="sub" style="margin-bottom:14px;">
          Both servers were rendered to PDF and their pagination compared.
        </p>
        <div style="display:flex;gap:18px;flex-wrap:wrap;">
          <div style="flex:1;min-width:200px;border:1px solid #E2E8F0;border-radius:8px;padding:16px;">
            <div style="font-size:12px;color:#64748B;font-weight:600;text-transform:uppercase;letter-spacing:.4px;">SSRS pages</div>
            <div style="font-size:30px;font-weight:700;color:#0F172A;margin-top:4px;">${sp ?? "—"}</div>
          </div>
          <div style="flex:1;min-width:200px;border:1px solid #E2E8F0;border-radius:8px;padding:16px;">
            <div style="font-size:12px;color:#64748B;font-weight:600;text-transform:uppercase;letter-spacing:.4px;">Bold Reports pages</div>
            <div style="font-size:30px;font-weight:700;color:${measured && sp === dp ? "#059669" : "#DC2626"};margin-top:4px;">${dp ?? "—"}</div>
          </div>
        </div>
        <div style="margin-top:16px;padding:12px 14px;background:#F8FAFC;border:1px solid #E2E8F0;border-radius:6px;font-size:13px;color:#334155;">
          ${escapeHtml(v.renderSummary || "No render summary available.")}
        </div>
        <div style="margin-top:12px;padding:12px 14px;background:#FFFBEB;border:1px solid #FDE68A;border-radius:6px;font-size:12.5px;color:#92400E;">
          <strong>Pixel comparison is not performed.</strong> SSRS and Bold Reports use different PDF
          writers, so comparing their output byte-for-byte would describe the encoders rather than the
          report. Confirm appearance in the Web Report Designer tab.
        </div>
        <p class="sub" style="margin-top:12px;font-size:12px;">
          Compared at ${v.runUtc ? new Date(v.runUtc).toLocaleString() : "unknown time"}
          on parameter set <code>${escapeHtml((v.parameterSetHash || "").slice(0, 12))}</code>.
        </p>
      </div>
    `;
    return;
  }

  if (currentDiffMode === "data") {
    if (!v || !v.validated) {
      grid.innerHTML = notValidatedPanel("Data comparison");
      return;
    }
    const matched = v.rowsSource === v.rowsDestination;
    grid.innerHTML = `
      <div class="card" style="grid-column: 1 / -1; padding: 20px;">
        <h2>Data Comparison for ${name}</h2>
        <table style="margin-top:10px;">
          <thead><tr><th>Measure</th><th>SSRS (source)</th><th>Bold Reports (destination)</th><th>Result</th></tr></thead>
          <tbody>
            <tr>
              <td>Rows returned</td>
              <td>${v.rowsSource ?? 0}</td>
              <td>${v.rowsDestination ?? 0}</td>
              <td><span class="pill ${matched ? "ok" : "fail"}">${matched ? "Match" : "Mismatch"}</span></td>
            </tr>
            <tr>
              <td>Cell-level data accuracy</td>
              <td colspan="2" style="text-align:center;">${fmtPct(v.dataAccuracy)}</td>
              <td><span class="pill ${v.dataAccuracy >= 99 ? "ok" : "warn"}">${v.dataAccuracy >= 99 ? "Within threshold" : "Below threshold"}</span></td>
            </tr>
          </tbody>
        </table>
        <p class="sub" style="margin-top:12px;font-size:12px;">
          Both servers were rendered to CSV with the same parameter set and compared row by row.
        </p>
      </div>
    `;
    return;
  }

  // Side-by-side default view — shows the measured comparison, not a mock render.
  // Embedding a true visual preview requires the Bold Reports Viewer widget; use the
  // "Web Report Designer" tab for that.
  if (!v || !v.validated) {
    grid.innerHTML = notValidatedPanel("Side-by-side comparison");
    return;
  }

  const dataOk = v.dataAccuracy >= 99;
  const rowsOk = v.rowsSource === v.rowsDestination;
  const pagesMeasured = v.sourcePageCount != null && v.destinationPageCount != null;
  const pagesOk = pagesMeasured && v.sourcePageCount === v.destinationPageCount;
  const allOk = dataOk && rowsOk && pagesOk;
  const pagePill = !pagesMeasured
    ? `<span class="pill warn" title="${escapeHtml(v.renderSummary || "")}">Not compared</span>`
    : `<span class="pill ${pagesOk ? "ok" : "fail"}">${pagesOk ? "Match" : "Mismatch"}</span>`;

  grid.innerHTML = `
    <div class="card" style="grid-column: 1 / -1; padding:20px;">
      <div style="display:flex;justify-content:space-between;align-items:center;margin-bottom:6px;">
        <h2>Source vs Destination — ${name}</h2>
        <span class="pill ${allOk ? "ok" : "warn"}">${allOk ? "Outputs match" : "Differences found"}</span>
      </div>
      <p class="sub" style="margin-bottom:16px;">
        Both servers rendered this report with the same parameters; the exports were compared directly.
      </p>
      <table>
        <thead>
          <tr><th>Check</th><th>SSRS</th><th>Bold Reports</th><th>Result</th></tr>
        </thead>
        <tbody>
          <tr>
            <td><strong>Rows returned</strong></td>
            <td>${v.rowsSource ?? 0}</td>
            <td>${v.rowsDestination ?? 0}</td>
            <td><span class="pill ${rowsOk ? "ok" : "fail"}">${rowsOk ? "Match" : "Mismatch"}</span></td>
          </tr>
          <tr>
            <td><strong>Data accuracy</strong></td>
            <td colspan="2" style="text-align:center;">${fmtPct(v.dataAccuracy)}</td>
            <td><span class="pill ${dataOk ? "ok" : "warn"}">${dataOk ? "Pass" : "Below threshold"}</span></td>
          </tr>
          <tr>
            <td><strong>Pages rendered</strong><div style="font-size:11.5px;color:#64748B;">PDF render from each server</div></td>
            <td>${v.sourcePageCount ?? "—"}</td>
            <td>${v.destinationPageCount ?? "—"}</td>
            <td>${pagePill}</td>
          </tr>
        </tbody>
      </table>
      <div style="margin-top:12px;padding:10px 12px;background:#F8FAFC;border:1px solid #E2E8F0;border-radius:6px;font-size:12.5px;color:#475569;">
        ${escapeHtml(v.renderSummary || "No render summary available.")}
        <div style="margin-top:4px;color:#64748B;">
          Pixel-level layout is not compared automatically — open the report in the Bold Reports
          Designer to confirm appearance.
        </div>
      </div>
      <div style="margin-top:16px;display:flex;gap:10px;flex-wrap:wrap;">
        <button class="btn2 btn-sm" onclick="setDiffView('designer')">Open the live report in Bold Reports Designer</button>
        <button class="btn2 btn-sm" onclick="setDiffView('rdl')">Compare RDL definitions</button>
      </div>
      <p class="sub" style="margin-top:12px;font-size:12px;">
        Last compared ${v.runUtc ? new Date(v.runUtc).toLocaleString() : "unknown"}. Current state:
        <strong>${escapeHtml(v.state || "Unknown")}</strong>.
      </p>
    </div>
  `;
}

function notValidatedPanel(what) {
  return `
    <div class="card" style="grid-column: 1 / -1; padding:28px; text-align:center;">
      <div style="font-size:15px;font-weight:600;color:#0F172A;margin-bottom:6px;">
        ${escapeHtml(what)} not available yet
      </div>
      <p class="sub" style="max-width:520px;margin:0 auto 16px;">
        This report has not been validated. Run a validation pass to render it on both SSRS and
        Bold Reports and compare the real output.
      </p>
      <button class="btn" onclick="runValidationPass()">Run Validation</button>
    </div>
  `;
}

async function loadReportRdlDiff(report) {
  if (!currentRun || !report) return;

  const origEl = document.getElementById("rdl-code-original");
  const xfrmEl = document.getElementById("rdl-code-transformed");

  try {
    const data = await Api.getReportRdl(currentRun.id, report.id);

    // Never substitute invented XML for a definition the server did not return —
    // a fake diff would hide a real transformation problem.
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

async function initBoldReportDesigner(report, reportPath) {
  const container = document.getElementById("bold-report-designer");
  const loader = document.getElementById("bold-designer-loading-overlay");
  if (!container) return;

  try {
    const config = await Api.getDesignerConfig(currentRun.id);
    const serviceUrl = config.serviceUrl || "http://localhost:8090/reporting/reportservice/api/Designer";
    const reportServerUrl = config.reportServerUrl || "http://localhost:8090/reporting/api/site/site1";
    const token = config.serviceAuthorizationToken || "";

    if (loader) loader.style.display = "none";
    container.style.display = "block";

    if (window.jQuery && typeof window.jQuery(container).boldReportDesigner === 'function') {
      try { window.jQuery(container).boldReportDesigner("destroy"); } catch (e) { }

      window.jQuery(container).boldReportDesigner({
        serviceUrl: serviceUrl,
        reportServerUrl: reportServerUrl,
        serviceAuthorizationToken: token,
        reportPath: reportPath,
        toolbarSettings: (window.ej && window.ej.ReportDesigner) ? {
          items: ej.ReportDesigner.ToolbarItems.All
        } : undefined,
        reportLoaded: function (args) {
          showToast(`Report '${report.name}' loaded in Bold Web Report Designer.`);
        },
        reportSaved: function (args) {
          showToast(`Report '${report.name}' saved to Bold Reports Server.`);
        },
        ajaxBeforeLoad: function (args) {
          if (token && args.headers) {
            args.headers.Authorization = token;
          }
        }
      });
    } else {
      // Fallback
      container.innerHTML = `
        <div style="padding:40px 20px;text-align:center;color:#475569;background:#FFFFFF;border-radius:8px;margin:20px;">
          <div style="font-size:24px;margin-bottom:10px;">📊</div>
          <h3 style="font-size:16px;color:#0F172A;margin-bottom:8px;">Bold Reports Web Designer</h3>
          <p style="font-size:13px;max-width:550px;margin:0 auto 16px auto;line-height:1.5;">
            Server Integration configured for <code>${escapeHtml(reportServerUrl)}</code>.<br>
            Active Report: <code>${escapeHtml(reportPath)}</code>
          </p>
          <div style="display:flex;justify-content:center;gap:10px;">
            <a href="${escapeHtml(config.serverUrl)}/reporting/site/${escapeHtml(config.tenantId || 'site1')}/reports" target="_blank" class="btn" style="text-decoration:none;">
              Launch Full Designer Studio ↗
            </a>
            <button class="btn2" onclick="setDiffView('rdl')">Inspect Transformed RDL Code</button>
          </div>
        </div>
      `;
    }
  } catch (err) {
    if (loader) loader.innerHTML = `<div style="color:#EF4444;font-weight:600;">Designer connection error: ${escapeHtml(err.message)}</div>`;
  }
}

function openDesignerSimulation() {
  setDiffView("designer");
}

async function approveReportValidation() {
  if (!currentRun || !selectedValidationReportId) return;
  await Api.approveReport(currentRun.id, selectedValidationReportId, "Approved via Validation viewer");
  showToast("Report validation approved.");
  await refreshLibraryState();
}

async function flagReportReview() {
  if (!currentRun || !selectedValidationReportId) return;
  await Api.flagReport(currentRun.id, selectedValidationReportId, "Flagged for Report Designer inspection");
  showToast("Report flagged for Report Designer review.");
  await refreshLibraryState();
}

// -----------------------------------------------------------------------------
// Step 6: Cutover & Audit
// -----------------------------------------------------------------------------
async function refreshCutoverState() {
  if (!currentRun) return;
  try {
    const gatesRes = await Api.getCutoverGates(currentRun.id);
    const container = document.getElementById("cutover-gates-list");
    const advisory = new Set(gatesRes?.advisoryGates || []);
    if (gatesRes && gatesRes.gates && container) {
      container.innerHTML = gatesRes.gates.map(g => {
        // An advisory gate is reported but does not block cutover, so it must not be
        // styled as a blocker.
        const isAdvisory = advisory.has(g.name);
        const label = g.passed ? "Passed" : (isAdvisory ? "Optional" : "Action Required");
        const cls = g.passed ? "ok" : (isAdvisory ? "" : "warn");
        return `
        <div style="display:flex;align-items:center;justify-content:space-between;padding:10px 14px;background:#F8FAFC;border:1px solid #E2E8F0;border-radius:8px;">
          <div>
            <strong style="color:#0F172A;font-size:13.5px;">${escapeHtml(g.name)}</strong>
            <div style="color:#64748B;font-size:12px;margin-top:2px;">${escapeHtml(g.detail)}</div>
          </div>
          <span class="pill ${cls}">${label}</span>
        </div>
      `;
      }).join("");
    }

    // Footer must agree with the gates above it.
    const footer = document.getElementById("cutover-footer-status");
    if (footer) {
      const blocking = (gatesRes?.gates || []).filter(g => !g.passed && !advisory.has(g.name));
      if (gatesRes?.ready) {
        footer.innerHTML = '<span style="color:#059669;font-weight:600;">✓ All blocking gates passed — ready for cutover sign-off.</span>';
      } else {
        footer.innerHTML = `<span style="color:#B45309;font-weight:600;">${blocking.length} gate(s) still need attention before cutover.</span>`;
      }
    }

    const banner = document.getElementById("cutover-blocked-banner");
    if (banner) {
      banner.style.display = gatesRes && gatesRes.ready ? "none" : "block";
    }

    const btnCutover = document.getElementById("btn-confirm-cutover");
    if (btnCutover) {
      btnCutover.disabled = !(gatesRes && gatesRes.ready);
      btnCutover.style.opacity = gatesRes && gatesRes.ready ? "1" : "0.55";
      btnCutover.title = gatesRes && gatesRes.ready ? "Confirm production cutover" : "Upload reports in Step 4 before cutover";
    }
  } catch { }
}

async function handleConfirmCutover() {
  if (!currentRun) return;
  const gatesRes = await Api.getCutoverGates(currentRun.id);
  if (!gatesRes || !gatesRes.ready) {
    alert("⚠️ Cutover Blocked:\n\nNo reports have been uploaded to Bold Reports Server yet.\n\nPlease connect to Bold Reports in Step 1 and execute Step 4: Upload Pipeline first.");
    return;
  }

  if (!confirm("Are you ready to finalize production cutover? This will configure URL shims and activate Bold Reports as primary.")) return;
  try {
    const res = await Api.confirmCutover(currentRun.id);
    if (res.success) {
      showToast("Production cutover completed! Bold Reports is now the primary reporting server.");
      await refreshCutoverState();
    } else {
      alert("Cutover failed: " + res.message);
    }
  } catch (err) {
    alert("Cutover failed: " + err.message);
  }
}

async function downloadReportPdf() {
  if (!currentRun) return;
  window.open(`/api/diagnostics/runs/${currentRun.id}/report-pdf`, "_blank");
}

async function downloadSupportBundle() {
  if (!currentRun) return;
  window.open(`/api/diagnostics/runs/${currentRun.id}/support-bundle`, "_blank");
}

// -----------------------------------------------------------------------------
// Standalone Tools: Local Folder & Expression Tester
// -----------------------------------------------------------------------------
function toggleLfAuth() {
  const mode = document.getElementById("lf-auth-mode").value;
  document.getElementById("lf-creds-box").style.display = mode === "creds" ? "grid" : "none";
  document.getElementById("lf-token-box").style.display = mode === "token" ? "block" : "none";
}

// Fills the folder fields with the conventional SSRS export layout so the shape of the
// expected input is obvious. These are examples, not paths that exist on this machine.
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
        if (typeof l === 'string') return `<div style="font-family:Consolas, monospace;font-size:11.5px;color:#CBD5E1;line-height:1.5;">${escapeHtml(l)}</div>`;
        const timeStr = l.timestamp ? new Date(l.timestamp).toLocaleTimeString() : "";
        return `<div style="font-family:Consolas, monospace;font-size:11.5px;color:#CBD5E1;line-height:1.5;">[${timeStr}] ${escapeHtml(l.message || JSON.stringify(l))}</div>`;
      }).join("");
      logBox.scrollTop = logBox.scrollHeight;
    }
  }, 1000);

  try {
    const res = await Api.runLocalFolderMigration(request);
    clearInterval(logTimer);

    document.getElementById("lf-status-summary").innerText = res.message;
    document.getElementById("lf-rep-stat").innerText = `${res.migratedReports} / ${res.totalReports}`;
    document.getElementById("lf-ds-stat").innerText = `${res.migratedDataSources} / ${res.totalDataSources}`;
    document.getElementById("lf-dset-stat").innerText = `${res.migratedDataSets} / ${res.totalDataSets}`;

    if (res.logs && res.logs.length > 0) {
      logBox.innerHTML = res.logs.map(l => {
        if (typeof l === 'string') return `<div style="font-family:Consolas, monospace;font-size:11.5px;color:#CBD5E1;line-height:1.5;">${escapeHtml(l)}</div>`;
        const timeStr = l.timestamp ? new Date(l.timestamp).toLocaleTimeString() : "";
        return `<div style="font-family:Consolas, monospace;font-size:11.5px;color:#CBD5E1;line-height:1.5;">[${timeStr}] ${escapeHtml(l.message || JSON.stringify(l))}</div>`;
      }).join("");
      logBox.scrollTop = logBox.scrollHeight;
    }

    const linksDiv = document.getElementById("lf-download-links");
    linksDiv.innerHTML = `
      <span class="pill ok" style="padding:6px 12px;">Migration Completed</span>
      ${res.resultCsvPath ? `<span class="pill" style="background:#F1F5F9;border:1px solid #CBD5E1;">CSV Summary: ${escapeHtml(res.resultCsvPath)}</span>` : ""}
      ${res.summaryMarkdownPath ? `<span class="pill" style="background:#F1F5F9;border:1px solid #CBD5E1;">Markdown Summary: ${escapeHtml(res.summaryMarkdownPath)}</span>` : ""}
    `;

    showToast("Local folder migration completed successfully.");
  } catch (err) {
    clearInterval(logTimer);
    document.getElementById("lf-status-summary").innerText = `Migration failed: ${err.message}`;
    showToast(`Error: ${err.message}`);
  }
}

function loadSelectedSample(idx) {
  if (!expressionSamples || !expressionSamples[idx]) return;
  const s = expressionSamples[idx];
  document.getElementById("expr-input").value = s.expression;
  document.getElementById("expr-fields-json").value = s.sampleDataJson || "{}";
  document.getElementById("expr-params-json").value = s.parametersJson || "{}";
  evaluateExpression();
}

async function evaluateExpression() {
  const expr = document.getElementById("expr-input").value;
  const fields = document.getElementById("expr-fields-json").value;
  const params = document.getElementById("expr-params-json").value;

  try {
    const res = await Api.evaluateExpression({
      expression: expr,
      sampleDataJson: fields,
      parametersJson: params
    });

    const pill = document.getElementById("expr-status-pill");
    if (res.compatibilityStatus === "Compatible") {
      pill.className = "pill ok";
      pill.innerText = "Compatible";
    } else if (res.compatibilityStatus === "NeedsReview") {
      pill.className = "pill warn";
      pill.innerText = "Needs Review";
    } else {
      pill.className = "pill fail";
      pill.innerText = "Blocked";
    }

    document.getElementById("expr-category-text").innerText = res.category || "Standard";
    document.getElementById("expr-explanation-text").innerText = res.explanation || "Computes report values.";
    document.getElementById("expr-eval-output").innerText = res.evaluatedValue || "No value returned";
    document.getElementById("expr-guidance-text").innerText = res.migrationAdvice || "Ready for Bold Reports import.";
  } catch (err) {
    showToast("Evaluation failed: " + err.message);
  }
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
    set("setting-vis-threshold", s["Validation.VisualSimilarityThreshold"]);
    set("setting-pixel-tolerance", s["Validation.PixelTolerance"]);
    set("setting-extract-parallel", s["Extraction.MaxParallel"]);
    set("setting-upload-parallel", s["Upload.MaxParallel"]);
    set("setting-row-limit", s["Validation.SampleRowLimit"]);

    const shim = document.getElementById("setting-shim-enabled");
    if (shim && s["UrlShim.Enabled"] !== undefined) shim.checked = !!s["UrlShim.Enabled"];
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
    "Validation.VisualSimilarityThreshold": num("setting-vis-threshold", 95.0),
    "Validation.PixelTolerance": Math.round(num("setting-pixel-tolerance", 2)),
    "Validation.SampleRowLimit": Math.round(num("setting-row-limit", 10000)),
    "Extraction.MaxParallel": Math.round(num("setting-extract-parallel", 4)),
    "Upload.MaxParallel": Math.round(num("setting-upload-parallel", 4)),
    "UrlShim.Enabled": document.getElementById("setting-shim-enabled")?.checked ?? false,
    "UrlShim.ListenPath": "/ReportServer"
  };

  try {
    await Api.updateSettings(payload);
    showToast("Settings saved. New thresholds apply to the next validation pass.");
    await loadSettingsView();
  } catch (err) {
    showToast("Save failed: " + err.message);
  }
}

async function handleLandingFileScan(event) {
  const file = event.target.files[0];
  if (!file) return;

  const resultsDiv = document.getElementById("landing-scan-results");
  resultsDiv.style.display = "block";
  resultsDiv.innerHTML = `<div class="card" style="padding:16px;"><strong>Analyzing ${escapeHtml(file.name)}...</strong></div>`;

  try {
    const formData = new FormData();
    formData.append("file", file);
    const res = await fetch("/api/scanner/analyse", { method: "POST", body: formData });
    const data = await res.json();

    let badgeClass = "pill ok";
    if (data.bucket === "NeedsReview") badgeClass = "pill warn";
    if (data.bucket === "Blocked") badgeClass = "pill fail";

    resultsDiv.innerHTML = `
      <div class="card" style="padding:16px;">
        <div style="display:flex;justify-content:space-between;align-items:center;">
          <div>
            <strong>${escapeHtml(data.fileName)}</strong>
            <span style="color:#64748B;font-size:12px;margin-left:8px;">Schema: ${escapeHtml(data.schemaVersion)}</span>
          </div>
          <span class="${badgeClass}">${escapeHtml(data.bucket)}</span>
        </div>
        <p style="margin:8px 0 0;font-size:13px;color:#334155;">${escapeHtml(data.message)}</p>
      </div>
    `;
  } catch (err) {
    resultsDiv.innerHTML = `<div class="card" style="padding:16px;color:#DC2626;">Scan failed: ${escapeHtml(err.message)}</div>`;
  }
}

// -----------------------------------------------------------------------------
// Poller & Helpers
// -----------------------------------------------------------------------------
function startPoller() {
  if (pollTimer) clearInterval(pollTimer);
  pollTimer = setInterval(async () => {
    if (!currentRun) return;
    try {
      const poll = await Api.pollState(currentRun.id);
      if (poll) {
        updateJourneyBadges(poll);
        if (currentTab === "dashboard") refreshDashboardState(poll);
        if (currentTab === "migration") refreshMigrationState(poll);
      }
    } catch { }
  }, 3000);
}

function updateJourneyBadges(poll) {
  const sideCount = document.getElementById("side-count-reports");
  if (sideCount) sideCount.innerText = poll.reportsTotal || 0;

  const step1Sub = document.getElementById("side-step1-sub");
  if (step1Sub && poll.itemsTotal > 0) {
    step1Sub.innerText = `${poll.itemsTotal} items discovered`;
  }

  const step3Sub = document.getElementById("side-step3-sub");
  if (step3Sub && poll.reportsTotal > 0) {
    const score = Math.round(((poll.reportsClean + poll.reportsAutoFixed) / poll.reportsTotal) * 100);
    step3Sub.innerText = `Readiness: ${score}%`;
  }

  const step4Sub = document.getElementById("side-step4-sub");
  if (step4Sub && poll.reportsUploaded > 0) {
    step4Sub.innerText = `${poll.reportsUploaded}/${poll.reportsTotal} uploaded`;
  }
}

function showToast(msg) {
  let toast = document.getElementById("app-toast");
  if (!toast) {
    toast = document.createElement("div");
    toast.id = "app-toast";
    toast.style.cssText = "position:fixed;bottom:24px;right:24px;background:#0F172A;color:#FFFFFF;padding:12px 20px;border-radius:8px;font-size:13.5px;font-weight:500;box-shadow:0 10px 25px -3px rgba(0,0,0,0.25);z-index:9999;transition:opacity 0.2s ease;";
    document.body.appendChild(toast);
  }
  toast.innerText = msg;
  toast.style.opacity = "1";
  setTimeout(() => { if (toast) toast.style.opacity = "0"; }, 3500);
}

function escapeHtml(str) {
  if (!str) return "";
  return String(str)
    .replace(/&/g, "&amp;")
    .replace(/</g, "&lt;")
    .replace(/>/g, "&gt;")
    .replace(/"/g, "&quot;")
    .replace(/'/g, "&#039;");
}
