// Bold Migrate — Core Shell, Navigation, Activity & Poller

// Bold Migrate — Enterprise Frontend Controller
// Realistic Sequential Migration Journey

let currentTab = "connections";
let currentRun = null;
let pollTimer = null;
let reportsCache = [];
let selectedReportIds = new Set();
// Auto-select-all must happen once per run, not on every visit to Step 2.
let selectedReportsInitialized = false;
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
    libBody.addEventListener("change", (ev) => {
      const box = ev.target.closest("[data-lib-action='toggle']");
      if (!box) return;
      handleReportCheckboxChange(box.dataset.reportId, box.checked);
    });
    libBody.addEventListener("click", (ev) => {
      const empty = ev.target.closest("[data-lib-empty-action]");
      if (empty) {
        if (empty.dataset.libEmptyAction === "retry") refreshLibraryState();
        else switchTab("connections");
        return;
      }
      const btn = ev.target.closest("[data-lib-action]");
      if (!btn) return;
      const id = btn.dataset.reportId;
      const action = btn.dataset.libAction;
      const rep = (reportsCache || []).find(x => x.id === id);
      if (action === "validate") selectReportForValidation(id);
    });
  }

  // Rebind buttons carry RDL-controlled text (reference names come from the source server).
  // They MUST stay out of inline handlers: the browser HTML-decodes an attribute before the JS
  // parser sees it, so escapeHtml does not protect a JS string literal built inside onclick.
  const depHost = document.getElementById("dep-verify-results");
  if (depHost && !depHost.dataset.delegated) {
    depHost.dataset.delegated = "1";
    depHost.addEventListener("click", (ev) => {
      const hide = ev.target.closest("[data-dep-action='hide']");
      if (hide) {
        depHost.style.display = "none";
        return;
      }
      const btn = ev.target.closest("[data-dep-action='rebind']");
      if (!btn) return;
      rebindBrokenLink(
        btn.dataset.reportId,
        btn.dataset.linkType,
        btn.dataset.reference,
        btn.dataset.selectId
      );
    });
  }

  const flaggedHost = document.getElementById("dash-flagged-list");
  if (flaggedHost && !flaggedHost.dataset.delegated) {
    flaggedHost.dataset.delegated = "1";
    flaggedHost.addEventListener("click", (ev) => {
      const btn = ev.target.closest("[data-flagged-action='inspect']");
      if (!btn) return;
      selectReportForValidation(btn.dataset.reportId);
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
// View & Tab Switching (4-Step Guided Studio + Settings Utilities Modal)
// -----------------------------------------------------------------------------
const TAB_ALIASES = {
  library: "assess",
  dashboard: "assess",
  migration: "publish",
  cutover: "publish",
};

function switchTab(tabName) {
  if (tabName === "settings" || tabName === "bold-delete") {
    if (typeof openSettingsModal === "function") {
      openSettingsModal(tabName === "bold-delete" ? "cleanup" : "tuning");
    }
    return;
  }

  let resolvedTab = TAB_ALIASES[tabName] || tabName;
  if (tabName === "local-folder") {
    resolvedTab = "connections";
  }

  currentTab = resolvedTab;
  updateMilestoneProgression(resolvedTab);

  // Update main 4-step panes with smooth slide animation
  document.querySelectorAll(".tab-pane").forEach(pane => {
    pane.classList.remove("is-active");
    pane.style.display = "none";
  });
  const targetPane = document.getElementById(`tab-${resolvedTab}`);
  if (targetPane) {
    targetPane.style.display = "block";
    void targetPane.offsetWidth;
    targetPane.classList.add("is-active");
  }

  if (tabName === "local-folder" && typeof setSourceEntryMode === "function") {
    setSourceEntryMode("folder", document.getElementById("choice-folder"));
  }

  // Keep Step 2 multi-select floating bar scoped to Assess step
  if (typeof updateSelectedCountUI === "function") {
    updateSelectedCountUI();
  }

  // Tab specific refreshes
  if (resolvedTab === "connections") refreshConnectionsState();
  if (resolvedTab === "assess") {
    refreshLibraryState();
    refreshDashboardState();
  }
  if (resolvedTab === "validation") refreshValidationState();
  if (resolvedTab === "publish") {
    refreshMigrationState();
    refreshCutoverState();
  }

  highlightNextAction(resolvedTab);
}

/// Marks the single control that moves the journey forward on the current step.
/// Nothing previously distinguished "the button that continues" from the half-dozen other
/// buttons on a pane, which is the reported "I wasn't sure which button to click next".
const NEXT_ACTION_BY_STEP = {
  connections: "btn-discover-primary",
  assess: "btn-footer-migrate",
  validation: "btn-validation-continue",
  publish: "btn-start-migration-tab"
};

function highlightNextAction(tabName) {
  document.querySelectorAll(".is-next-action").forEach(el => el.classList.remove("is-next-action"));

  const id = NEXT_ACTION_BY_STEP[tabName];
  if (!id) return;
  const el = document.getElementById(id);
  // Only cue a control the operator can actually use right now.
  if (el && !el.disabled) el.classList.add("is-next-action");
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
  { tab: "connections", name: "Source & Target" },
  { tab: "assess",      name: "Assess & Resolve" },
  { tab: "validation",  name: "Visual Verify" },
  { tab: "publish",     name: "Publish & Cutover" },
];

function updateMilestoneProgression(activeTab) {
  const activeIdx = JOURNEY_STEPS.findIndex(s => s.tab === activeTab);
  JOURNEY_STEPS.forEach((step, idx) => {
    const el = document.getElementById(`mstep-${step.tab}`);
    if (!el) return;
    el.classList.toggle("active", idx === activeIdx);
    if (idx === activeIdx) el.setAttribute("aria-current", "step");
    else el.removeAttribute("aria-current");
  });
}

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
  updateMilestoneProgression(activeTab);
}

function showLandingView() {
  switchTab("connections");
  if (typeof setSourceEntryMode === "function") setSourceEntryMode("scan");
  window.scrollTo(0, 0);
}

function showAppView(initialTab = "connections") {
  watchStatTiles();
  switchTab(initialTab);
}

function initSidebarState() {}
function toggleSidebar() {}
function applySidebarState() {}


function startPoller() {
  if (pollTimer) clearInterval(pollTimer);
  pollTimer = setInterval(async () => {
    if (!currentRun) return;
    try {
      const poll = await Api.pollState(currentRun.id);
      if (poll) {
        updateJourneyBadges(poll);
        syncServerStageActivity(poll);
        if (currentTab === "assess") {
          refreshDashboardState(poll);
        }
        if (currentTab === "publish") {
          refreshMigrationState(poll);
        }
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
  const ssrsOk = ["Connected", "Fetching", "Analysed", "Uploading", "Uploaded", "Validating", "Validated", "CutOver"].includes(poll.status);
  const setMilestone = (id, isDone) => {
    const el = document.getElementById(id);
    if (el) el.classList.toggle("done", !!isDone);
  };
  const reportsTotal = poll.reportsTotal || 0;
  const reportsValidated = poll.reportsValidated || 0;
  const reportsUploaded = poll.reportsUploaded || 0;

  setMilestone("mstep-connections", ssrsOk || (poll.itemsTotal || 0) > 0);
  setMilestone("mstep-assess", reportsTotal > 0);
  setMilestone("mstep-validation", reportsValidated > 0);
  setMilestone("mstep-publish", poll.status === "CutOver");

  const sub1 = document.getElementById("mstep1-sub");
  const sub2 = document.getElementById("mstep2-sub");
  const sub3 = document.getElementById("mstep3-sub");
  const sub4 = document.getElementById("mstep4-sub");
  if (sub1) sub1.textContent = reportsTotal > 0 ? `${reportsTotal} Reports Loaded` : (ssrsOk ? "Connected" : "Connect or Drop .RDL");
  if (sub2) sub2.textContent = reportsTotal > 0 ? `${reportsTotal} Assessed` : "Catalog & Links";
  if (sub3) sub3.textContent = reportsValidated > 0 ? `${reportsValidated}/${reportsTotal || 0} Validated` : "Playwright & Diff";
  if (sub4) sub4.textContent = poll.status === "CutOver" ? "Cutover Complete" : (reportsUploaded > 0 ? `${reportsUploaded}/${reportsTotal || 0} Uploaded` : "4-Stage & URL Shim");

  const topbarStatus = document.getElementById("topbar-run-status");
  const topbarDot = document.getElementById("topbar-pulse-dot");
  if (topbarStatus) {
    const runName = (currentRun && currentRun.name) ? currentRun.name : "Active Run";
    const countSuffix = reportsTotal > 0 ? ` · ${reportsTotal} reports` : "";
    topbarStatus.textContent = `${runName} · ${poll.status || "Ready"}${countSuffix}`;
  }
  if (topbarDot) {
    topbarDot.classList.remove("warn", "info");
    if (["Fetching", "Validating", "Uploading"].includes(poll.status)) {
      topbarDot.classList.add("info");
    } else if (poll.status !== "CutOver" && !ssrsOk) {
      topbarDot.classList.add("warn");
    }
  }
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

/// Toasts.
///
/// The previous implementation created ONE fixed div at z-index 9999 and only faded its opacity
/// to 0 when done. It kept `pointer-events: auto` and stayed in the layout, so after the first
/// toast ever fired there was a permanently invisible ~260x44 click-blocking rectangle pinned to
/// the bottom-right of every screen — on top of modals (z 200), the drawer (z 181) and the Bold
/// Reports designer. That is why buttons near the bottom of the window stopped responding.
///
/// The container is pointer-events: none so a toast can never eat a click, each toast is removed
/// from the DOM when it expires, and messages stack instead of overwriting one another.
function getToastContainer() {
  let host = document.getElementById("app-toast-host");
  if (!host) {
    host = document.createElement("div");
    host.id = "app-toast-host";
    host.className = "bm-toast-host";
    // Announced politely so async results reach screen readers, which the old toast never did.
    host.setAttribute("role", "status");
    host.setAttribute("aria-live", "polite");
    document.body.appendChild(host);
  }
  return host;
}

function showToast(msg, isError = false) {
  if (!msg) return;
  const host = getToastContainer();

  const toast = document.createElement("div");
  toast.className = "bm-toast" + (isError ? " is-error" : "");
  toast.innerText = String(msg);
  host.appendChild(toast);

  // Let the element land before transitioning, so the entry animation actually runs.
  requestAnimationFrame(() => toast.classList.add("is-visible"));

  const life = isError ? 7000 : 3800;
  const remove = () => {
    toast.classList.remove("is-visible");
    setTimeout(() => toast.remove(), 260);
  };

  // Each toast owns its own timer, so a second message no longer inherits the first one's
  // countdown and vanish early.
  setTimeout(remove, life);

  while (host.childElementCount > 4) host.firstElementChild?.remove();
}

/// Polls run state until `isDone(poll)` is true, or the deadline passes.
///
/// The loops this replaces ran a fixed number of iterations and then fell through to a success
/// toast, so on a large estate the UI announced "Catalog discovered and assessed!" while the
/// server was still fetching. The caller now gets an explicit `timedOut` and can say so.
async function pollUntil(isDone, { intervalMs = 600, timeoutMs = 120000, onTick = null } = {}) {
  if (!currentRun) return { ok: false, timedOut: false, poll: null };

  const deadline = Date.now() + timeoutMs;
  let last = null;

  while (Date.now() < deadline) {
    await new Promise(r => setTimeout(r, intervalMs));

    try {
      last = await Api.pollState(currentRun.id);
    } catch {
      continue; // transient; keep trying until the deadline
    }

    if (!last) continue;
    updateJourneyBadges(last);
    if (onTick) {
      try { onTick(last, Math.min(1, 1 - (deadline - Date.now()) / timeoutMs)); } catch { }
    }
    if (isDone(last)) return { ok: true, timedOut: false, poll: last };
  }

  return { ok: false, timedOut: true, poll: last };
}

function escapeHtml(str) {
  // Note: a plain `if (!str)` used to blank out the number 0 and the string "0".
  if (str === null || str === undefined) return "";
  return String(str)
    .replace(/&/g, "&amp;")
    .replace(/</g, "&lt;")
    .replace(/>/g, "&gt;")
    .replace(/"/g, "&quot;")
    .replace(/'/g, "&#039;");
}

