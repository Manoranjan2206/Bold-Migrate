// Bold Migrate — Core Shell, Navigation, Activity & Poller

// Bold Migrate — Enterprise Frontend Controller
// Realistic Sequential Migration Journey

let currentTab = "connections";
let currentRun = null;
let pollTimer = null;
let reportsCache = [];
let selectedReportIds = new Set();
let rdlCache = {};
let dataSourcesCache = [];
let currentLibFilter = "all";
let isPaused = false;
let currentDiffMode = "side-by-side";
let selectedValidationReportId = null;
let latestValidation = null;
let destinationSessionCache = { destinationUrl: "", bearerToken: "" };
let valRenderVersion = Date.now();

document.addEventListener("DOMContentLoaded", async () => {
  // Chrome first: these are pure DOM and must not depend on the API coming up.
  initSidebarState();
  renderSteppers();
  watchStatTiles();
  bindDynamicActionDelegation();
  if (typeof bindModalAccessibility === "function") bindModalAccessibility();
  await initApp();
});

function bindDynamicActionDelegation() {
  const libBody = document.getElementById("library-table-body");
  if (libBody && !libBody.dataset.delegated) {
    libBody.dataset.delegated = "1";
    libBody.addEventListener("click", (ev) => {
      const btn = ev.target.closest("[data-lib-action]");
      if (!btn) return;
      const id = btn.dataset.reportId;
      const action = btn.dataset.libAction;
      const rep = (reportsCache || []).find(x => x.id === id);
      if (action === "validate") selectReportForValidation(id);
      else if (action === "delete" && rep) deleteSingleBoldReport(rep.id, rep.name);
    });
  }

  const valGrid = document.getElementById("val-evidence-grid");
  if (valGrid && !valGrid.dataset.delegated) {
    valGrid.dataset.delegated = "1";
    valGrid.addEventListener("click", (ev) => {
      const el = ev.target.closest("[data-val-action]");
      if (!el) return;
      const action = el.dataset.valAction;
      const id = el.dataset.reportId;
      const r = validationSummaryCache?.reports?.find(x => x.reportId === id);
      if (!r) return;
      if (action === "validate") runSingleReportValidation(r.reportId);
      else if (action === "viewer") openInteractiveViewer(r.reportId, r.path, "staged");
      else if (action === "designer") openInteractiveDesigner(r.reportId, r.path, "staged");
      else if (action === "diff") openRdlDiffModal(r.reportId);
      else if (action === "compare") openVisualCompareModal(r.reportId);
      else if (action === "logs") openRenderLogs(r.path);
      else if (action === "zoom" && r.screenshotUrl) openSnapshotZoom(`${r.screenshotUrl}?t=${valRenderVersion}`, r.name);
    });
  }

  const srvBody = document.getElementById("server-uploaded-reports-tbody");
  if (srvBody && !srvBody.dataset.delegated) {
    srvBody.dataset.delegated = "1";
    srvBody.addEventListener("click", (ev) => {
      const btn = ev.target.closest("[data-srv-action]");
      if (!btn) return;
      const action = btn.dataset.srvAction;
      const id = btn.dataset.reportId;
      const r = (reportsCache || []).find(x => x.id === id);
      if (!r) return;
      const cat = r.folder ? r.folder.replace(/^\//, "") : "General";
      const serverReportPath = `${cat}/${r.name}`;
      if (action === "viewer") openInteractiveViewer(r.id, serverReportPath, "server");
      else if (action === "designer") openInteractiveDesigner(r.id, serverReportPath, "server");
    });
  }
}

// -----------------------------------------------------------------------------
// Global & Per-Process Progress / Loading Indicator Coordinator
// -----------------------------------------------------------------------------
const activeOperations = new Map();

function beginActivity(id, title, detail = "", pct = null) {
  activeOperations.set(id, { title, detail, pct });
  renderGlobalActivityBar();
}

function updateActivity(id, detail, pct = null, title = null) {
  const existing = activeOperations.get(id);
  if (!existing) return;
  if (title !== null) existing.title = title;
  if (detail !== undefined && detail !== null) existing.detail = detail;
  if (pct !== undefined) existing.pct = pct;
  renderGlobalActivityBar();
}

function endActivity(id) {
  activeOperations.delete(id);
  renderGlobalActivityBar();
}

function renderGlobalActivityBar() {
  const bar = document.getElementById("global-activity-bar");
  if (!bar) return;

  if (activeOperations.size === 0) {
    bar.style.display = "none";
    return;
  }

  // Show the most recently updated operation first
  const entries = Array.from(activeOperations.values());
  const latest = entries[entries.length - 1];
  const extraCount = entries.length > 1 ? ` (+${entries.length - 1} active)` : "";

  const titleEl = document.getElementById("global-activity-title");
  const detailEl = document.getElementById("global-activity-detail");
  const pctEl = document.getElementById("global-activity-pct");
  const fillEl = document.getElementById("global-activity-progress");

  if (titleEl) titleEl.innerText = `${latest.title}${extraCount}`;
  if (detailEl) detailEl.innerText = latest.detail || "Please wait...";

  if (latest.pct !== null && Number.isFinite(latest.pct)) {
    const clamped = Math.max(0, Math.min(100, Math.round(latest.pct)));
    if (pctEl) pctEl.innerText = `${clamped}%`;
    if (fillEl) {
      fillEl.classList.remove("is-indeterminate");
      fillEl.style.width = `${clamped}%`;
    }
  } else {
    if (pctEl) pctEl.innerText = "Running...";
    if (fillEl) {
      fillEl.style.width = "";
      fillEl.classList.add("is-indeterminate");
    }
  }

  bar.style.display = "block";
}

function setButtonLoading(btnOrId, loading, loadingText = "Processing...") {
  const btn = typeof btnOrId === "string" ? document.getElementById(btnOrId) : btnOrId;
  if (!btn) return;
  if (loading) {
    if (!btn.dataset.origHtml) {
      btn.dataset.origHtml = btn.innerHTML;
      btn.dataset.origDisabled = btn.disabled ? "1" : "0";
    }
    btn.disabled = true;
    btn.setAttribute("data-loading", "true");
    btn.innerHTML = `<span class="bm-spin"></span>${escapeHtml(loadingText)}`;
  } else {
    if (btn.dataset.origHtml !== undefined) {
      btn.innerHTML = btn.dataset.origHtml;
      delete btn.dataset.origHtml;
    }
    btn.removeAttribute("data-loading");
    if (btn.dataset.origDisabled !== undefined) {
      btn.disabled = btn.dataset.origDisabled === "1";
      delete btn.dataset.origDisabled;
    } else {
      btn.disabled = false;
    }
  }
}

async function withButtonProgress(btnOrId, buttonLabel, activityTitle, fn, activityDetail = "") {
  const opId = `op-${Date.now()}-${Math.random().toString(36).slice(2, 7)}`;
  setButtonLoading(btnOrId, true, buttonLabel);
  beginActivity(opId, activityTitle, activityDetail || buttonLabel, null);
  try {
    return await fn(opId);
  } finally {
    endActivity(opId);
    setButtonLoading(btnOrId, false);
  }
}

async function initApp() {
  try {
    // Resume existing active migration run across reloads; explicit New Run is available in sidebar.
    const existingRun = await Api.getActiveRun();
    currentRun = existingRun || await Api.createOrGetRun("SSRS Live Migration", "");

    // Load global security/preview checkbox & conflict policy state
    try {
      const s = await Api.getSettings();
      const trustCb = document.getElementById("conn-ds-trust-cert");
      if (trustCb && s) trustCb.checked = !!s["Preview.AllowUntrustedSqlCert"];
      const policyEl = document.getElementById("upload-conflict-policy");
      if (policyEl && s && s["Upload.ConflictPolicy"]) policyEl.value = s["Upload.ConflictPolicy"];
    } catch { }

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

async function startFreshRun() {
  const confirmed = await showConfirmDialog({
    title: "Start Fresh Migration Run",
    message: "Start a brand-new migration run? This will archive the current active run and clear temporary staged progress.",
    confirmText: "Start Fresh Run"
  });
  if (!confirmed) return;
  beginActivity("new-run", "Starting Fresh Migration Run", "Archiving previous run and initializing workspace...", null);
  try {
    const ssrsUrl = (document.getElementById("conn-ssrs-url")?.value || "").trim();
    currentRun = await Api.createOrGetRun("SSRS Live Migration", ssrsUrl, true);
    resetLocalRunState();
    destinationSessionCache = { destinationUrl: "", bearerToken: "" };
    valRenderVersion = Date.now();
    await refreshConnectionsState();
    await refreshLibraryState();
    await refreshDashboardState();
    switchTab("connections");
    showToast("Started a fresh migration run.");
  } catch (err) {
    showToast("Could not start fresh run: " + err.message);
  } finally {
    endActivity("new-run");
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
  if (tabName === "bold-delete") syncBulkDeleteFromActiveRun();
}

function syncBulkDeleteFromActiveRun() {
  const urlEl = document.getElementById("bd-url");
  const emailEl = document.getElementById("bd-email");
  const statusEl = document.getElementById("bd-status");
  if (!urlEl) return;

  const activeDestUrl = (currentRun?.destinationServerUrl || destinationSessionCache.destinationUrl || document.getElementById("conn-dest-url")?.value || "").trim();
  const activeEmail = (currentRun?.destinationUserEmail && currentRun.destinationUserEmail !== "BearerTokenAuth"
    ? currentRun.destinationUserEmail
    : document.getElementById("conn-dest-email")?.value || "").trim();

  if (!urlEl.value.trim() && activeDestUrl) {
    urlEl.value = activeDestUrl;
    if (emailEl && !emailEl.value.trim() && activeEmail) emailEl.value = activeEmail;
    if (statusEl && !statusEl.textContent.trim()) {
      statusEl.textContent = "Pre-filled Bold Reports URL from Step 1. Enter password to load reports.";
    }
  }
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
    `<button type="button" class="journey-step" onclick="switchTab('${s.tab}')">` +
      `<span class="jstep-num">${i + 1}</span>` +
      `<span class="jstep-name">${s.name}</span>` +
    `</button>`
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

// -----------------------------------------------------------------------------
// Sidebar Collapse / Expand Persistence
// -----------------------------------------------------------------------------
const SIDEBAR_COLLAPSE_STORAGE_KEY = "bm_sidebar_collapsed";

function initSidebarState() {
  let collapsed = false;
  try {
    collapsed = localStorage.getItem(SIDEBAR_COLLAPSE_STORAGE_KEY) === "true";
  } catch {
    collapsed = false;
  }
  applySidebarState(collapsed);
}

function toggleSidebar() {
  const sidebar = document.getElementById("app-sidebar");
  if (!sidebar) return;
  const nextCollapsed = !sidebar.classList.contains("is-collapsed");
  applySidebarState(nextCollapsed);
  try {
    localStorage.setItem(SIDEBAR_COLLAPSE_STORAGE_KEY, String(nextCollapsed));
  } catch {
    // Ignore storage quota or private browsing errors
  }
  // Allow embedded viewers/designers inside .main to reflow cleanly
  window.dispatchEvent(new Event("resize"));
  setTimeout(() => window.dispatchEvent(new Event("resize")), 220);
}

function applySidebarState(collapsed) {
  const sidebar = document.getElementById("app-sidebar");
  if (!sidebar) return;
  sidebar.classList.toggle("is-collapsed", !!collapsed);

  const btn = document.getElementById("btn-toggle-sidebar");
  if (btn) {
    const label = collapsed ? "Expand sidebar" : "Collapse sidebar";
    btn.setAttribute("aria-expanded", String(!collapsed));
    btn.setAttribute("aria-label", label);
    btn.title = label;
  }
}


function startPoller() {
  if (pollTimer) clearInterval(pollTimer);
  pollTimer = setInterval(async () => {
    if (!currentRun) return;
    try {
      const poll = await Api.pollState(currentRun.id);
      if (poll) {
        updateJourneyBadges(poll);
        syncServerStageActivity(poll);
        if (currentTab === "dashboard") refreshDashboardState(poll);
        if (currentTab === "migration") refreshMigrationState(poll);
      }
    } catch { }
  }, 2000);
}

function syncServerStageActivity(poll) {
  if (!poll || !poll.status) return;
  if (poll.status === "Uploading") {
    const total = poll.reportsTotal || 0;
    const done = poll.reportsUploaded || 0;
    const pct = total > 0 ? Math.min(99, Math.round((done / total) * 100)) : null;
    if (activeOperations.has("upload-pipeline")) {
      updateActivity("upload-pipeline", `Uploaded ${done} of ${total} report(s) to Bold Reports...`, pct);
    } else {
      beginActivity("upload-pipeline", "Batch Upload Pipeline Running", `Uploaded ${done} of ${total} report(s) to Bold Reports...`, pct);
    }
  } else if (activeOperations.has("upload-pipeline")) {
    endActivity("upload-pipeline");
  }
}

function updateJourneyBadges(poll) {
  const sideCount = document.getElementById("side-count-reports");
  if (sideCount) sideCount.innerText = poll.reportsTotal || 0;

  const step1Sub = document.getElementById("side-step1-sub");
  if (step1Sub) {
    step1Sub.innerText = poll.itemsTotal > 0 ? `${poll.itemsTotal} items discovered` : "SSRS & Bold Reports";
  }

  const step3Sub = document.getElementById("side-step3-sub");
  if (step3Sub) {
    if (poll.reportsTotal > 0) {
      const score = Math.round(((poll.reportsClean + poll.reportsAutoFixed) / poll.reportsTotal) * 100);
      step3Sub.innerText = `Readiness: ${score}%`;
    } else {
      step3Sub.innerText = "Readiness & Blockers";
    }
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

  const toggleDone = (id, done) => {
    const el = document.getElementById(id);
    if (el) el.classList.toggle("is-done", !!done);
  };
  const ssrsOk = ["Connected", "Fetching", "Analysed", "Uploading", "Uploaded", "Validating", "Validated", "CutOver"].includes(poll.status);
  toggleDone("nav-connections", ssrsOk || poll.itemsTotal > 0);
  toggleDone("nav-library", (poll.reportsTotal || 0) > 0);
  toggleDone("nav-dashboard", (poll.reportsTotal || 0) > 0);
  toggleDone("nav-validation", (poll.reportsValidated || 0) > 0);
  toggleDone("nav-migration", (poll.reportsUploaded || 0) > 0);
  toggleDone("nav-cutover", poll.status === "CutOver");
}

function clearFieldError(inputOrId) {
  const el = typeof inputOrId === "string" ? document.getElementById(inputOrId) : inputOrId;
  if (!el) return;
  el.classList.remove("is-invalid");
  const next = el.nextElementSibling;
  if (next && next.classList.contains("field-error-msg")) {
    next.remove();
  }
}

function validateRequiredInput(inputOrId, message) {
  const el = typeof inputOrId === "string" ? document.getElementById(inputOrId) : inputOrId;
  if (!el) return true;
  if ((el.value || "").trim()) {
    clearFieldError(el);
    return true;
  }

  el.classList.add("is-invalid");
  let errEl = el.nextElementSibling;
  if (!errEl || !errEl.classList.contains("field-error-msg")) {
    errEl = document.createElement("span");
    errEl.className = "field-error-msg";
    el.insertAdjacentElement("afterend", errEl);
  }
  errEl.innerText = message || "This field is required.";

  if (!el.dataset.valBound) {
    el.dataset.valBound = "1";
    el.addEventListener("input", () => {
      if ((el.value || "").trim()) clearFieldError(el);
    });
  }
  el.focus();
  return false;
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

