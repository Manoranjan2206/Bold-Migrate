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
  // Chrome first: these are pure DOM and must not depend on the API coming up.
  renderSteppers();
  watchStatTiles();
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

// The six step panes each carry an empty .journey-stepper placeholder; this
// fills them from one definition, so the rail is edited in a single place
// instead of six hand-maintained copies.
const JOURNEY_STEPS = [
  { tab: "connections", name: "Connect & Discover" },
  { tab: "library",     name: "Report Library" },
  { tab: "dashboard",   name: "Assessment" },
  { tab: "validation",  name: "Validate" },
  { tab: "migration",   name: "Upload" },
  { tab: "cutover",     name: "Cutover" },
];

function renderSteppers() {
  const markup = JOURNEY_STEPS.map((s, i) =>
    `<div class="journey-step" onclick="switchTab('${s.tab}')">` +
      `<span class="jstep-num">${i + 1}</span>` +
      `<span class="jstep-name">${s.name}</span>` +
    `</div>`
  ).join('<div class="jstep-divider"></div>');

  document.querySelectorAll(".journey-stepper[data-stepper]").forEach(el => {
    el.innerHTML = markup;
  });
}

// A stat of zero carries no information, so it drops to grey and only a
// non-zero count keeps its status colour. Watching the tiles avoids hooking
// each of the ~7 places that write these numbers.
function greyZeroStats() {
  document.querySelectorAll(".tile .n").forEach(el => {
    const raw = (el.innerText || "").trim();
    const zero = raw === "0" || raw === "—" || raw === "-" || /^0\s*\/\s*\d+$/.test(raw);
    el.classList.toggle("is-zero", zero);
  });
}

function watchStatTiles() {
  greyZeroStats();
  const obs = new MutationObserver(() => greyZeroStats());
  document.querySelectorAll(".tiles").forEach(t =>
    obs.observe(t, { childList: true, characterData: true, subtree: true })
  );
}

function updateStepperProgression(activeTab) {
  const stepOrder = ["connections", "library", "dashboard", "validation", "migration", "cutover"];
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
  renderSteppers();
  watchStatTiles();
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
  if (dot) dot.style.background = ok ? "var(--br-success)" : "var(--br-text-subtle)";
}

function renderDataSourcesTable() {
  const tbody = document.getElementById("conn-datasources-table");
  if (!tbody) return;

  if (!dataSourcesCache || dataSourcesCache.length === 0) {
    tbody.innerHTML = `
      <tr>
        <td colspan="7" style="text-align:center;color:var(--br-text-muted);padding:20px;">
          No data sources discovered yet. Click "Save & Discover Catalog" to scan SSRS.
        </td>
      </tr>
    `;
    return;
  }

  tbody.innerHTML = dataSourcesCache.map(ds => {
    const mode = ds.credentialRetrieval || (ds.hasCredentials ? "Store" : "Prompt");
    const isStore = mode === "Store";
    const isIntegrated = mode === "Integrated";
    const isNone = mode === "None";
    const isPrompt = mode === "Prompt";

    return `
    <tr>
      <td><strong>${escapeHtml(ds.name)}</strong><br><small style="color:var(--br-text-muted);">${escapeHtml(ds.sourcePath || ds.parentPath || "/")}</small></td>
      <td>
        <div style="display:flex;align-items:center;gap:4px;">
          <input id="ds-conn-${ds.id}" class="ds-conn-str" value="${escapeHtml(ds.connectString || "")}"
                 placeholder="Server=...;Database=..."
                 style="width:220px;padding:4px 8px;border:1px solid var(--br-border-strong);border-radius:4px;font-family:monospace;font-size:12px;">
          ${(ds.connectString && (ds.connectString.toLowerCase().includes("localhost") || ds.connectString.includes("127.0.0.1"))) ? `
            <button type="button" class="btn2 btn-sm" style="font-size:10px;padding:2px 6px;white-space:nowrap;" title="Replace localhost with host.docker.internal for Bold Reports in Docker" onclick="replaceLocalhostWithDocker('${ds.id}')">Docker Fix</button>
          ` : ''}
        </div>
      </td>
      <td><span class="pill" style="font-size:11px;">${escapeHtml(ds.serverType || ds.itemType || "SQL")}</span></td>
      <td>
        <select id="ds-mode-${ds.id}" onchange="onDataSourceModeChange('${ds.id}')"
                style="padding:4px 6px;border:1px solid var(--br-border-strong);border-radius:4px;font-size:12px;">
          <option value="Integrated" ${isIntegrated ? "selected" : ""}>Integrated (Windows Login)</option>
          <option value="Store" ${isStore ? "selected" : ""}>Store (Saved Account)</option>
          <option value="Prompt" ${isPrompt ? "selected" : ""}>Prompt (At Runtime)</option>
          <option value="None" ${isNone ? "selected" : ""}>None (In Connection String)</option>
        </select>
      </td>
      <td>
        <input id="ds-user-${ds.id}" data-ds-id="${ds.id}" class="ds-cred-user" placeholder="${isStore ? "e.g. sa or DOMAIN\\user" : "N/A"}"
               value="${escapeHtml(ds.userName || "")}" aria-label="User for ${escapeHtml(ds.name)}"
               ${!isStore ? "disabled" : ""}
               style="width:130px;padding:4px 8px;border:1px solid var(--br-border-strong);border-radius:4px;${!isStore ? "background:var(--br-surface-hover);opacity:0.6;" : ""}">
      </td>
      <td>
        <input type="password" id="ds-pass-${ds.id}" data-ds-id="${ds.id}" class="ds-cred-pass"
               placeholder="${isStore ? (ds.hasCredentials ? "Stored (unchanged)" : "Password") : "N/A"}"
               aria-label="Password for ${escapeHtml(ds.name)}"
               autocomplete="new-password"
               ${!isStore ? "disabled" : ""}
               style="width:120px;padding:4px 8px;border:1px solid var(--br-border-strong);border-radius:4px;${!isStore ? "background:var(--br-surface-hover);opacity:0.6;" : ""}">
      </td>
      <td style="white-space:nowrap;">
        <span class="pill ${isIntegrated || ds.hasCredentials || isNone ? "ok" : ""}" id="ds-status-${ds.id}">
          ${isIntegrated ? "Windows Auth" : (ds.hasCredentials ? "Stored" : (isNone ? "None" : "Prompt"))}
        </span>
        <button class="btn2 btn-sm" style="margin-left:6px;" onclick="saveDataSourceCredentials('${ds.id}')">Save</button>
      </td>
    </tr>
  `}).join("");
}

function onDataSourceModeChange(dsId) {
  const mode = document.getElementById(`ds-mode-${dsId}`)?.value;
  const userEl = document.getElementById(`ds-user-${dsId}`);
  const passEl = document.getElementById(`ds-pass-${dsId}`);
  const isStore = mode === "Store";

  if (userEl) {
    userEl.disabled = !isStore;
    userEl.style.background = isStore ? "" : "var(--br-surface-hover)";
    userEl.style.opacity = isStore ? "1" : "0.6";
    userEl.placeholder = isStore ? "e.g. sa or DOMAIN\\user" : "N/A";
  }
  if (passEl) {
    passEl.disabled = !isStore;
    passEl.style.background = isStore ? "" : "var(--br-surface-hover)";
    passEl.style.opacity = isStore ? "1" : "0.6";
    passEl.placeholder = isStore ? "Password" : "N/A";
  }
}

function replaceLocalhostWithDocker(dsId) {
  const connEl = document.getElementById(`ds-conn-${dsId}`);
  if (connEl && connEl.value) {
    connEl.value = connEl.value.replace(/localhost/gi, "host.docker.internal").replace(/127\.0\.0\.1/g, "host.docker.internal");
    showToast("Replaced localhost with host.docker.internal. Remember to click Save.");
  }
}

async function saveDataSourceCredentials(dsId) {
  if (!currentRun) return;
  const mode = document.getElementById(`ds-mode-${dsId}`)?.value || "Prompt";
  const user = document.getElementById(`ds-user-${dsId}`)?.value.trim() || "";
  const pass = document.getElementById(`ds-pass-${dsId}`)?.value || "";
  const connectString = document.getElementById(`ds-conn-${dsId}`)?.value.trim() || "";
  const pill = document.getElementById(`ds-status-${dsId}`);
  const ds = dataSourcesCache.find(d => d.id === dsId);

  try {
    const res = await Api.setDataSourceCredentials(currentRun.id, dsId, user, pass, mode, connectString);
    if (ds) {
      ds.credentialRetrieval = mode;
      if (connectString) ds.connectString = connectString;
      ds.hasCredentials = res.hasCredentials;
      ds.userName = mode === "Store" && res.hasCredentials ? user : null;
    }
    const passEl = document.getElementById(`ds-pass-${dsId}`);
    if (passEl && mode === "Store") {
      passEl.value = "";
      passEl.placeholder = res.hasCredentials ? "Stored (unchanged)" : "Password";
    }
    if (pill) {
      const isOk = mode === "Integrated" || res.hasCredentials || mode === "None";
      pill.className = isOk ? "pill ok" : "pill";
      pill.innerText = mode === "Integrated" ? "Windows Auth" : (res.hasCredentials ? "Stored" : (mode === "None" ? "None" : "Prompt"));
    }
    showToast(`Data source settings updated (${mode}).`);
  } catch (err) {
    showToast(`Failed to save: ${err.message}`, true);
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
  const url = (document.getElementById("conn-dest-url").value || "").trim();
  const mode = document.getElementById("conn-dest-auth-mode").value;
  const email = (document.getElementById("conn-dest-email").value || "").trim();
  const pass = document.getElementById("conn-dest-pass").value;
  const token = (document.getElementById("conn-dest-token").value || "").trim();

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
        <td colspan="8" style="text-align:center;color:var(--br-text-muted);padding:32px;">
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

  showToast(`Saving selection of ${selectedReportIds.size} reports and navigating to validation & preview...`);

  try {
    await Api.selectReports(currentRun.id, Array.from(selectedReportIds));
    switchTab("validation");
    await initValidationTab();
  } catch (err) {
    showToast("Validation navigation error: " + err.message);
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
  showToast("Re-evaluating compatibility rules...");
  await Api.checkEstate(currentRun.id);
  await refreshDashboardState();
  showToast("Assessment updated.");
}

// -----------------------------------------------------------------------------
// Step 5: Migration Pipeline
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
        if (typeof l === 'string') return `<div class="log-line">${escapeHtml(l)}</div>`;
        const timeStr = l.timestamp ? new Date(l.timestamp).toLocaleTimeString() : "";
        const lvl = (l.level || "INFO").toUpperCase();
        let lvlClass = "";
        if (lvl.includes("ERR")) lvlClass = " is-err";
        else if (lvl.includes("WARN")) lvlClass = " is-warn";
        else if (lvl.includes("INFO")) lvlClass = " is-info";
        const code = l.eventCode ? `[${escapeHtml(l.eventCode)}] ` : "";
        const msg = l.message || "";
        return `<div class="log-line">
          <span class="log-ts">[${timeStr}]</span> 
          <span class="log-lvl${lvlClass}">[${escapeHtml(lvl)}]</span> 
          <span class="log-code">${code}</span>
          <span class="log-msg">${escapeHtml(msg)}</span>
        </div>`;
      }).join("");
      logBox.scrollTop = logBox.scrollHeight;
    }
    await loadServerUploadedReports();
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
  if (box) box.innerHTML = "<div class='log-empty'>Log cleared.</div>";
}

// -----------------------------------------------------------------------------
// Step 4: Automated Render Validation & Interactive Inspection
// -----------------------------------------------------------------------------
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
    return `<span class="pill warn" style="font-size:11px;font-weight:600;cursor:pointer;" onclick="openRenderLogs('${escapeHtml(r.path)}')" title="Click to view log">⚠ Needs Review</span>`;
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
    grid.innerHTML = `
      <div class="card" style="grid-column: 1 / -1; padding: 36px; text-align: center; color: var(--br-text-muted);">
        <svg width="32" height="32" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="1.5" style="margin-bottom: 8px; opacity: 0.6;">
          <circle cx="11" cy="11" r="8"></circle><line x1="21" y1="21" x2="16.65" y2="16.65"></line>
        </svg>
        <p style="margin: 0; font-size: 14px; font-weight: 500;">No reports match the current filter (${currentValFilter}).</p>
      </div>`;
    return;
  }

  if (currentValViewMode === "details") {
    grid.style.display = "block";
    const allChecked = filtered.length > 0 && filtered.every(r => selectedValReportIds.has(r.reportId));
    grid.innerHTML = `
      <div class="card" style="padding:0;overflow:hidden;">
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
                ? `<img src="${r.screenshotUrl}?t=${Date.now()}" alt="${escapeHtml(r.name)}" onclick="openSnapshotZoom('${r.screenshotUrl}', '${escapeHtml(r.name)}')" style="width:68px;height:42px;object-fit:cover;object-position:top;border-radius:4px;border:1px solid var(--br-border);cursor:pointer;" title="Click to expand snapshot">`
                : `<span style="font-size:11px;color:var(--br-text-subtle);">—</span>`;
              const detailMsg = r.status === "DataSourceOffline"
                ? `<span style="color:var(--br-info-text);">RDL valid (DB offline in local preview)</span>`
                : (r.errorMessage ? `<span style="color:var(--br-warning-text);">${escapeHtml(r.errorMessage)}</span>` : `<span style="color:var(--br-text-muted);">Ready</span>`);

              return `
                <tr>
                  <td class="check-cell"><input type="checkbox" ${isSel ? "checked" : ""} onchange="toggleValReportSelection('${r.reportId}', this.checked)"></td>
                  <td>${thumbCell}</td>
                  <td><strong>${escapeHtml(r.name)}</strong><br><small style="color:var(--br-text-muted);font-family:monospace;">${escapeHtml(r.path)}</small></td>
                  <td>${getValidationStatusBadge(r)}</td>
                  <td style="font-size:12px;max-width:340px;">${tagsHtml ? `<div style="margin-bottom:3px;">${tagsHtml}</div>` : ""}${detailMsg}</td>
                  <td style="text-align:right;white-space:nowrap;">
                    <button class="btn2 btn-sm" onclick="runSingleReportValidation('${r.reportId}')" title="Run Playwright validation on this report only">▶ Validate</button>
                    <button class="btn2 btn-sm" onclick="openInteractiveViewer('${r.reportId}', '${escapeHtml(r.path)}', 'staged')" style="margin-left:4px;">👁 View</button>
                    <button class="btn2 btn-sm" onclick="openInteractiveDesigner('${r.reportId}', '${escapeHtml(r.path)}', 'staged')" style="margin-left:4px;">✏ Edit</button>
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
    else if (r.status === "NeedsManualCheck" || r.status === "Failed") borderStyle = "border: 1px solid var(--br-warning-border);";

    // Thumbnail Preview
    let thumbHtml = "";
    if (r.screenshotUrl) {
      thumbHtml = `
        <div style="height:150px;width:100%;background:var(--br-surface-alt);border-bottom:1px solid var(--br-border);position:relative;overflow:hidden;cursor:pointer;" onclick="openSnapshotZoom('${r.screenshotUrl}', '${escapeHtml(r.name)}')">
          <img src="${r.screenshotUrl}?t=${Date.now()}" alt="${escapeHtml(r.name)}" style="width:100%;height:100%;object-fit:cover;object-position:top;" loading="lazy">
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
        <div style="height:140px;width:100%;background:var(--br-warning-bg);border-bottom:1px solid var(--br-warning-border);display:flex;flex-direction:column;align-items:center;justify-content:center;color:var(--br-warning);padding:12px;text-align:center;cursor:pointer;" onclick="openRenderLogs('${escapeHtml(r.path)}')">
          <svg width="28" height="28" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="1.8" style="margin-bottom:6px;"><path d="M12 9v2m0 4h.01m-6.938 4h13.856c1.54 0 2.502-1.667 1.732-3L13.732 4c-.77-1.333-2.694-1.333-3.464 0L3.34 16c-.77 1.333.192 3 1.732 3z"/></svg>
          <span style="font-size:12.5px;font-weight:600;">Needs Review</span>
          <small style="font-size:11px;color:var(--br-warning-text);margin-top:2px;max-width:90%;overflow:hidden;text-overflow:ellipsis;white-space:nowrap;">
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
          <div style="font-size:12px;color:var(--br-text-muted);margin-bottom:8px;font-family:monospace;">${escapeHtml(r.path)}</div>

          ${tagsHtml ? `<div style="display:flex;flex-wrap:wrap;gap:4px;margin-bottom:10px;">${tagsHtml}</div>` : ""}

          ${r.status === "DataSourceOffline" ? `
            <div style="font-size:11.5px;color:var(--br-info-text);background:var(--br-info-bg);border:1px solid var(--br-info-border);border-radius:6px;padding:6px 8px;margin-bottom:10px;">
              RDL syntax verified. Database connection is offline in local preview; report is ready for Server upload.
            </div>` : ""}

          ${r.errorMessage && r.status === "Failed" ? `
            <div style="font-size:11.5px;color:var(--br-warning-text);background:var(--br-warning-bg);border:1px solid var(--br-warning-border);border-radius:6px;padding:6px 8px;margin-bottom:10px;max-height:48px;overflow:hidden;text-overflow:ellipsis;">
              ${escapeHtml(r.errorMessage)}
            </div>` : ""}

          <div style="margin-top:auto;display:flex;justify-content:space-between;align-items:center;padding-top:10px;border-top:1px solid var(--br-border-subtle);">
            <div style="display:flex;gap:6px;">
              <button class="btn2 btn-sm" onclick="runSingleReportValidation('${r.reportId}')" title="Run Playwright validation on this report only">
                ▶ Validate
              </button>
              <button class="btn2 btn-sm" onclick="openInteractiveViewer('${r.reportId}', '${escapeHtml(r.path)}', 'staged')" title="Open interactive local viewer">
                👁 View
              </button>
              <button class="btn2 btn-sm" onclick="openInteractiveDesigner('${r.reportId}', '${escapeHtml(r.path)}', 'staged')" title="Edit in Web Report Designer">
                ✏ Edit
              </button>
            </div>
            ${r.status === "Failed" || r.status === "DataSourceOffline" ? `
              <button class="btn2 btn-sm" onclick="openRenderLogs('${escapeHtml(r.path)}')" style="padding:4px 8px;font-size:11px;color:var(--br-text-muted);">
                📜 Log
              </button>` : ""}
          </div>
        </div>
      </div>
    `;
  }).join("");
}

async function runSingleReportValidation(reportId) {
  if (!currentRun) return;
  showToast("Running render validation for selected report...");
  try {
    await Api.validateRender(currentRun.id, 1, [reportId]);
    setTimeout(() => loadValidationSummary(true), 2500);
    setTimeout(() => loadValidationSummary(true), 6000);
  } catch (err) {
    showToast("Validation failed to start: " + err.message, true);
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
  if (btnSel) btnSel.disabled = true;
  if (btnAll) btnAll.disabled = true;

  showToast(selectedOnly
    ? `Starting selective validation for ${targetIds.length} report(s)...`
    : "Starting automated render validation for all staged reports (5 parallel tabs)...");

  try {
    await Api.validateRender(currentRun.id, 5, targetIds);

    if (valPollInterval) clearInterval(valPollInterval);
    let attempts = 0;
    valPollInterval = setInterval(async () => {
      attempts++;
      await loadValidationSummary();

      const summary = validationSummaryCache;
      if (summary) {
        const notDone = selectedOnly
          ? summary.reports.filter(r => targetIds.includes(r.reportId) && r.status === "NotValidated").length
          : summary.reports.filter(r => r.status === "NotValidated").length;
        if (notDone === 0 || attempts >= 40) {
          clearInterval(valPollInterval);
          valPollInterval = null;
          if (btnSel) btnSel.disabled = false;
          if (btnAll) btnAll.disabled = false;
          showToast(`Validation finished! Passed: ${summary.passed}, Needs Review: ${summary.failed}, Needs Params: ${summary.needsManualCheck}`);
        }
      }
    }, 3000);
  } catch (err) {
    if (btnSel) btnSel.disabled = false;
    if (btnAll) btnAll.disabled = false;
    showToast("Validation failed to start: " + err.message, true);
  }
}

async function skipRenderValidationAndUpload() {
  if (!currentRun) return;
  if (!confirm("Skip local automated validation and proceed directly to Step 5: Upload Pipeline?")) return;

  try {
    await Api.skipValidation(currentRun.id);
    showToast("Validation skipped. Switching to Step 5: Upload Pipeline...");
    switchTab("migration");
  } catch (err) {
    showToast("Error skipping validation: " + err.message);
  }
}

async function resetValidationResults() {
  if (!currentRun) return;
  if (!confirm("Clear cached validation results and snapshots? Reports will return to 'Not Validated' until you run validation.")) return;
  try {
    await Api.clearValidationResults(currentRun.id);
    selectedValReportIds.clear();
    await loadValidationSummary(true);
    showToast("Validation results and cached screenshots cleared.");
  } catch (err) {
    showToast("Failed clearing validation results: " + err.message, true);
  }
}

// -----------------------------------------------------------------------------
// Interactive Local & Server Viewer/Designer Modals
// -----------------------------------------------------------------------------
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
    const dest = destinationConfigCache || {};
    const destUrl = dest.destinationUrl || "http://localhost:8090/reporting/site/site1";
    const serviceUrl = destUrl.replace(/\/site\/[^/]+/, "") + "/reporting/reportservice/api/Viewer";
    $(host).boldReportViewer({
      reportServiceUrl: serviceUrl,
      reportServerUrl: destUrl,
      reportPath: reportPath,
      serviceAuthorizationToken: dest.bearerToken || ""
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
    const dest = destinationConfigCache || {};
    const destUrl = dest.destinationUrl || "http://localhost:8090/reporting/site/site1";
    const serviceUrl = destUrl.replace(/\/site\/[^/]+/, "") + "/reporting/reportservice/api/Designer";
    $(host).boldReportDesigner({
      serviceUrl: serviceUrl,
      serverUrl: destUrl,
      serviceAuthorizationToken: dest.bearerToken || "",
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
    const uploaded = (res && res.reports) ? res.reports.filter(r => r.state === "Uploaded" || r.uploaded) : [];

    if (uploaded.length === 0) {
      tbody.innerHTML = `<tr><td colspan="4" style="text-align:center;color:var(--br-text-muted);padding:16px;">No reports have completed upload to Bold Reports Server yet.</td></tr>`;
      return;
    }

    tbody.innerHTML = uploaded.map(r => {
      const cat = r.folder ? r.folder.replace(/^\//, "") : "General";
      const serverReportPath = `${cat}/${r.name}`;
      return `
        <tr>
          <td><strong>${escapeHtml(r.name)}</strong></td>
          <td><code>${escapeHtml(cat)}</code></td>
          <td><span class="pill ok">Uploaded</span></td>
          <td style="text-align:right;white-space:nowrap;">
            <button class="btn2 btn-sm" onclick="openInteractiveViewer('${r.id}', '${escapeHtml(serverReportPath)}', 'server')" title="View directly on destination Bold Reports Server">
              View on Server
            </button>
            <button class="btn btn-sm" onclick="openInteractiveDesigner('${r.id}', '${escapeHtml(serverReportPath)}', 'server')" style="margin-left:6px;" title="Edit directly on destination Bold Reports Server">
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
        <div style="display:flex;align-items:center;justify-content:space-between;padding:10px 14px;background:var(--br-surface-alt);border:1px solid var(--br-border);border-radius:8px;">
          <div>
            <strong style="color:var(--br-text);font-size:13.5px;">${escapeHtml(g.name)}</strong>
            <div style="color:var(--br-text-muted);font-size:12px;margin-top:2px;">${escapeHtml(g.detail)}</div>
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
        footer.innerHTML = '<span style="color:var(--br-success);font-weight:600;">✓ All blocking gates passed — ready for cutover sign-off.</span>';
      } else {
        footer.innerHTML = `<span style="color:var(--br-warning-text);font-weight:600;">${blocking.length} gate(s) still need attention before cutover.</span>`;
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
      btnCutover.title = gatesRes && gatesRes.ready ? "Confirm production cutover" : "Upload reports in Step 5 before cutover";
    }
  } catch { }
}

async function handleConfirmCutover() {
  if (!currentRun) return;
  const gatesRes = await Api.getCutoverGates(currentRun.id);
  if (!gatesRes || !gatesRes.ready) {
    alert("⚠️ Cutover Blocked:\n\nNo reports have been uploaded to Bold Reports Server yet.\n\nPlease connect to Bold Reports in Step 1 and execute Step 5: Upload Pipeline first.");
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
        if (typeof l === 'string') return `<div class="log-line">${escapeHtml(l)}</div>`;
        const timeStr = l.timestamp ? new Date(l.timestamp).toLocaleTimeString() : "";
        return `<div class="log-line"><span class="log-ts">[${timeStr}]</span> ${escapeHtml(l.message || JSON.stringify(l))}</div>`;
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
            <span style="color:var(--br-text-muted);font-size:12px;margin-left:8px;">Schema: ${escapeHtml(data.schemaVersion)}</span>
          </div>
          <span class="${badgeClass}">${escapeHtml(data.bucket)}</span>
        </div>
        <p style="margin:8px 0 0;font-size:13px;color:var(--br-text-secondary);">${escapeHtml(data.message)}</p>
      </div>
    `;
  } catch (err) {
    resultsDiv.innerHTML = `<div class="card" style="padding:16px;color:var(--br-danger);">Scan failed: ${escapeHtml(err.message)}</div>`;
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
  if (step4Sub) {
    if (poll.reportsValidated > 0) {
      step4Sub.innerText = `${poll.reportsValidated}/${poll.reportsTotal || 0} validated`;
    } else {
      step4Sub.innerText = "Side-by-side & edit";
    }
  }

  const step5Sub = document.getElementById("side-step5-sub");
  if (step5Sub) {
    if (poll.reportsUploaded > 0) {
      step5Sub.innerText = `${poll.reportsUploaded}/${poll.reportsTotal || 0} uploaded`;
    } else {
      step5Sub.innerText = "4-stage batch upload";
    }
  }
}

function showToast(msg) {
  let toast = document.getElementById("app-toast");
  if (!toast) {
    toast = document.createElement("div");
    toast.id = "app-toast";
    toast.style.cssText = "position:fixed;bottom:24px;right:24px;background:var(--br-navy);color:var(--br-text-inverse);padding:12px 20px;border-radius:8px;font-size:13.5px;font-weight:500;box-shadow:0 10px 25px -3px rgba(0,0,0,0.25);z-index:9999;transition:opacity 0.2s ease;";
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
