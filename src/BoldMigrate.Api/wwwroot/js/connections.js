// Bold Migrate — Step 1: Connections & Data Sources

async function refreshConnectionsState() {
  if (!currentRun) return;
  try {
    // Show the servers this run is ACTUALLY pointed at. Leaving the defaults in the
    // fields would let "Test connection" silently repoint the run at the wrong host.
    hydrateConnectionFields();

    const poll = await Api.pollState(currentRun.id);
    const banner = document.getElementById("discovery-status-banner");
    const nextBtn = document.getElementById("btn-step1-next");
    if (poll && poll.itemsTotal > 0) {
      if (banner) banner.style.display = "flex";
      if (nextBtn) nextBtn.style.display = "inline-flex";

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
    } else {
      if (banner) banner.style.display = "none";
      if (nextBtn) nextBtn.style.display = "none";
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

  const modeEl = document.getElementById("conn-storage-mode");
  if (modeEl && currentRun.credentialMode) {
    modeEl.value = currentRun.credentialMode === "DpapiLocal" ? "DpapiLocal" : "InMemory";
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

  syncConnectionBridgeUI(ssrsConnected, destConnected);
  syncAuthPillsFromSelects();

  const discoveryBanner = document.getElementById("discovery-status-banner");
  const discoveryTitle = document.getElementById("discovery-banner-title");
  const discoveryDesc = document.getElementById("discovery-banner-desc");
  const step1NextBtn = document.getElementById("btn-step1-next");
  const hasCatalog = ["Analysed", "Uploading", "Uploaded", "Validating", "Validated", "CutOver"].includes(currentRun.status);
  if (discoveryBanner) discoveryBanner.style.display = hasCatalog ? "flex" : "none";
  if (step1NextBtn) step1NextBtn.style.display = hasCatalog ? "inline-flex" : "none";
  if (hasCatalog && discoveryTitle && discoveryDesc) {
    discoveryTitle.textContent = `Active Estate Discovered (${currentRun.status})`;
    discoveryDesc.textContent = currentRun.sourceServerUrl
      ? `Synced from ${currentRun.sourceServerUrl}`
      : "Catalog staged and ready for assessment.";
  }
}

function syncConnectionBridgeUI(ssrsConnected, destConnected) {
  const ssrsPill = document.getElementById("bridge-ssrs-pill");
  const ssrsLabel = document.getElementById("bridge-ssrs-label");
  if (ssrsPill) {
    ssrsPill.textContent = ssrsConnected ? `Connected · ${currentRun?.sourceVersion || "SSRS"}`.trim() : "Standby";
    ssrsPill.className = ssrsConnected ? "pill ok" : "pill";
  }
  if (ssrsLabel) {
    ssrsLabel.textContent = currentRun?.sourceServerUrl || "Read-only REST v2 / SOAP";
  }

  const destPill = document.getElementById("bridge-dest-pill");
  const destLabel = document.getElementById("bridge-dest-label");
  if (destPill) {
    destPill.textContent = destConnected ? "Connected" : "Standby";
    destPill.className = destConnected ? "pill ok" : "pill";
  }
  if (destLabel) {
    destLabel.textContent = currentRun?.destinationServerUrl || "Target Server 15.1.17";
  }

  const engPill = document.getElementById("bridge-engine-pill");
  if (engPill && currentRun?.status) {
    engPill.textContent = currentRun.status;
  }
}

function setSourceEntryMode(mode, cardEl) {
  ["live", "folder", "scan"].forEach(m => {
    const pane = document.getElementById(`src-mode-${m}`);
    const card = document.getElementById(`choice-${m}`);
    if (pane) pane.style.display = m === mode ? "block" : "none";
    if (card) card.classList.toggle("selected", m === mode);
  });
  if (cardEl) cardEl.classList.add("selected");
}

function setSsrsAuthPill(authValue, btnEl) {
  const sel = document.getElementById("conn-ssrs-auth");
  if (sel) sel.value = authValue;
  const wrap = document.getElementById("ssrs-auth-pills");
  if (wrap) {
    wrap.querySelectorAll(".seg-btn").forEach(b => {
      b.classList.toggle("active", b === btnEl || b.dataset.auth === authValue);
    });
  }
  toggleSsrsAuthFields();
}

function setDestAuthPill(modeValue, btnEl) {
  const sel = document.getElementById("conn-dest-auth-mode");
  if (sel) sel.value = modeValue;
  const wrap = document.getElementById("dest-auth-pills");
  if (wrap) {
    wrap.querySelectorAll(".seg-btn").forEach(b => {
      b.classList.toggle("active", b === btnEl || b.dataset.mode === modeValue);
    });
  }
  toggleDestAuthFields();
}

function syncAuthPillsFromSelects() {
  const ssrsSel = document.getElementById("conn-ssrs-auth");
  const ssrsWrap = document.getElementById("ssrs-auth-pills");
  if (ssrsSel && ssrsWrap) {
    ssrsWrap.querySelectorAll(".seg-btn").forEach(b => b.classList.toggle("active", b.dataset.auth === ssrsSel.value));
  }
  const destSel = document.getElementById("conn-dest-auth-mode");
  const destWrap = document.getElementById("dest-auth-pills");
  if (destSel && destWrap) {
    destWrap.querySelectorAll(".seg-btn").forEach(b => b.classList.toggle("active", b.dataset.mode === destSel.value));
  }
}

function openDataSourcesDrawer() {
  document.getElementById("drawer-ds-backdrop")?.classList.add("open");
  document.getElementById("drawer-datasources")?.classList.add("open");
}

function closeDataSourcesDrawer() {
  document.getElementById("drawer-ds-backdrop")?.classList.remove("open");
  document.getElementById("drawer-datasources")?.classList.remove("open");
}

function updateDataSourceBannerSummary() {
  const banner = document.getElementById("s1-ds-summary-banner");
  const title = document.getElementById("s1-ds-summary-title");
  const sub = document.getElementById("s1-ds-summary-sub");
  const btn = document.getElementById("btn-open-ds-drawer");
  const tbody = document.getElementById("conn-datasources-table");
  if (!banner || !title || !sub) return;

  banner.classList.remove("needs-action", "resolved");
  if (!dataSourcesCache || dataSourcesCache.length === 0) {
    banner.style.display = "none";
    return;
  }

  banner.style.display = "flex";
  const total = dataSourcesCache.length;
  const unverified = tbody ? tbody.querySelectorAll(".pill.warn, .pill.fail").length : 0;
  if (unverified > 0) {
    banner.classList.add("needs-action");
    title.textContent = `${unverified} of ${total} Data Source${total === 1 ? "" : "s"} Need Credential Verification`;
    sub.textContent = "Some discovered sources require stored database credentials or TLS validation before Step 3 & Step 4.";
    if (btn) btn.textContent = `Enter Credentials (${unverified}) →`;
  } else {
    banner.classList.add("resolved");
    title.textContent = `All ${total} Data Source${total === 1 ? "" : "s"} Ready`;
    sub.textContent = "Windows Integrated / validated credentials configured across discovered shared data sources.";
    if (btn) btn.textContent = `Review Data Sources (${total}) →`;
  }
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
  if (dot) {
    dot.style.background = ok ? "var(--br-success)" : "var(--br-text-subtle)";
    const item = dot.closest(".side-status-item");
    if (item) {
      const prefix = dotId === "side-ssrs-dot" ? "SSRS" : "Bold Reports";
      item.title = `${prefix}: ${label || (ok ? "Connected" : "Not connected")}`;
    }
  }
}

function renderDataSourcesTable() {
  const tbody = document.getElementById("conn-datasources-table");
  if (!tbody) return;
  queueMicrotask(updateDataSourceBannerSummary);

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
        <input id="ds-conn-${ds.id}" class="ds-conn-str" value="${escapeHtml(ds.connectString || "")}"
               data-initial="${escapeHtml(ds.connectString || "")}"
               placeholder="${ds.hasConnectString ? "Configured in staged .rsds" : "Server=...;Database=..."}">
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
        <button class="btn2 btn-sm" id="ds-btn-${ds.id}" style="margin-left:6px;" onclick="saveDataSourceCredentials('${ds.id}')">Validate &amp; Save</button>
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

let lastFailedDsId = null;

function showDataSourceError(dsName, message, detail, dsId = null, certUntrusted = false) {
  const box = document.getElementById("conn-ds-error-box");
  const titleEl = document.getElementById("conn-ds-error-title");
  const msgEl = document.getElementById("conn-ds-error-msg");
  const wrapEl = document.getElementById("conn-ds-error-details-wrap");
  const detailEl = document.getElementById("conn-ds-error-detail");
  const certFixWrap = document.getElementById("conn-ds-cert-fix-wrap");
  if (!box) return;

  lastFailedDsId = dsId;
  if (titleEl) titleEl.innerText = `Connection failed for '${dsName || "Data Source"}'`;
  if (msgEl) msgEl.innerText = message || "Could not connect to the data source server.";
  if (certFixWrap) {
    certFixWrap.style.display = certUntrusted ? "block" : "none";
  }
  if (wrapEl && detailEl) {
    if (detail && detail.trim()) {
      detailEl.innerText = detail;
      wrapEl.style.display = "block";
    } else {
      detailEl.innerText = "";
      wrapEl.style.display = "none";
    }
  }
  box.style.display = "block";
  box.scrollIntoView({ behavior: "smooth", block: "nearest" });
}

function hideDataSourceError() {
  lastFailedDsId = null;
  const box = document.getElementById("conn-ds-error-box");
  const certFixWrap = document.getElementById("conn-ds-cert-fix-wrap");
  if (certFixWrap) certFixWrap.style.display = "none";
  if (box) box.style.display = "none";
}

async function onToggleTrustSqlCert(checked) {
  beginActivity("trust-sql-cert", "Updating SQL Certificate Setting", checked ? "Enabling TrustServerCertificate for local validation & preview..." : "Requiring trusted SQL TLS certificates...", null);
  try {
    const s = await Api.getSettings();
    s["Preview.AllowUntrustedSqlCert"] = !!checked;
    await Api.updateSettings(s);
    const settingsToggle = document.getElementById("setting-allow-untrusted-sql");
    if (settingsToggle) settingsToggle.checked = !!checked;
    showToast(checked
      ? "Enabled trusting self-signed / internal SQL Server TLS certificates."
      : "Disabled trusting untrusted SQL Server TLS certificates.");
  } catch (err) {
    showToast("Could not update TLS certificate preference: " + err.message);
  } finally {
    endActivity("trust-sql-cert");
  }
}

async function trustSqlCertAndRetry() {
  const cb = document.getElementById("conn-ds-trust-cert");
  if (cb) cb.checked = true;
  await onToggleTrustSqlCert(true);
  if (lastFailedDsId) {
    await saveDataSourceCredentials(lastFailedDsId);
  }
}

async function onCredentialStorageModeChange(mode) {
  if (!currentRun) return;
  beginActivity("cred-mode", "Updating Credential Storage Mode", `Switching vault to ${mode}...`, null);
  try {
    const res = await Api.setCredentialMode(currentRun.id, mode);
    if (res && res.success) {
      currentRun.credentialMode = mode;
      showToast(`Credential vault switched to ${mode}.`);
    } else if (res?.message) {
      showToast(res.message);
    }
  } catch (err) {
    showToast("Could not update storage mode: " + err.message);
  } finally {
    endActivity("cred-mode");
  }
}

async function saveDataSourceCredentials(dsId) {
  if (!currentRun) return;
  const mode = document.getElementById(`ds-mode-${dsId}`)?.value || "Prompt";
  const user = document.getElementById(`ds-user-${dsId}`)?.value.trim() || "";
  const pass = document.getElementById(`ds-pass-${dsId}`)?.value || "";
  const connEl = document.getElementById(`ds-conn-${dsId}`);
  const rawConn = connEl?.value.trim() || "";
  // Do not echo redacted connection string masks back over the real staged value unless edited
  const connectString = connEl && rawConn === (connEl.dataset.initial || "").trim() ? "" : rawConn;
  const trustCert = document.getElementById("conn-ds-trust-cert")?.checked === true ? true : null;
  const pill = document.getElementById(`ds-status-${dsId}`);
  const btn = document.getElementById(`ds-btn-${dsId}`);
  const ds = dataSourcesCache.find(d => d.id === dsId);

  hideDataSourceError();
  if (pill) {
    pill.className = "pill run";
    pill.innerText = "Validating...";
  }

  return await withButtonProgress(
    btn,
    "Validating...",
    `Validating Data Source '${ds?.name || "SQL"}'`,
    async () => {
      try {
        const res = await Api.validateAndSaveDataSourceCredentials(
          currentRun.id, dsId, user, pass, mode, connectString, trustCert);
        if (!res || !res.success) {
          if (pill) {
            pill.className = "pill fail";
            pill.innerText = "Failed";
          }
          showDataSourceError(ds?.name || "Data Source", res?.message, res?.detail, dsId, !!res?.certUntrusted);
          showToast(`Validation failed: ${res?.message || "Connection error"}`);
          return;
        }

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
          pill.innerText = mode === "Integrated" ? "Windows Auth" : (res.hasCredentials ? "Verified" : (mode === "None" ? "None" : "Prompt"));
        }
        showToast(res.message || `Data source verified and saved (${mode}).`);
      } catch (err) {
        if (pill) {
          pill.className = "pill fail";
          pill.innerText = "Failed";
        }
        showDataSourceError(ds?.name || "Data Source", err.message, "", dsId, false);
        showToast(`Failed to validate: ${err.message}`);
      }
    },
    `Testing ${mode} connection to database server...`
  );
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
  if (!validateRequiredInput("conn-ssrs-url", "Please enter your SSRS Report Server URL.")) return;
  const url = document.getElementById("conn-ssrs-url").value.trim();
  const mode = document.getElementById("conn-ssrs-auth").value;
  const credMode = document.getElementById("conn-storage-mode")?.value || null;
  let user = "";
  let pass = "";
  if (mode !== "Integrated") {
    if (!validateRequiredInput("conn-ssrs-user", "Please enter a Windows/Basic username.")) return;
    user = document.getElementById("conn-ssrs-user").value;
    pass = document.getElementById("conn-ssrs-pass").value;
  }

  const pill = document.getElementById("conn-ssrs-status-pill");
  pill.className = "pill run";
  pill.innerText = "Probing...";

  await withButtonProgress(
    "btn-test-ssrs",
    "Probing SSRS...",
    "Testing SSRS Server Connection",
    async () => {
      const res = await Api.testConnection(currentRun.id, url, user, pass, mode, credMode);
      if (res.success) {
        pill.className = "pill ok";
        pill.innerText = `Connected · ${res.version || "REST v2.0"} (${res.api || "Auto"})`;
        showToast("SSRS Connection verified successfully.");
      } else {
        pill.className = "pill fail";
        pill.innerText = "Connection failed";
        showToast(`SSRS connection failed: ${res.message || "Unreachable"}`);
      }
    },
    `Probing REST v2.0 / SOAP 2010 endpoints at ${url}...`
  );
}

async function testDestConnection() {
  if (!currentRun) return;
  if (!validateRequiredInput("conn-dest-url", "Please enter your Bold Reports Server URL.")) return;
  const url = (document.getElementById("conn-dest-url").value || "").trim();
  const mode = document.getElementById("conn-dest-auth-mode").value;
  if (mode === "credentials" && !validateRequiredInput("conn-dest-email", "Please enter your Bold Reports admin email.")) return;
  if (mode === "token" && !validateRequiredInput("conn-dest-token", "Please enter a Bearer JWT token.")) return;
  const email = (document.getElementById("conn-dest-email").value || "").trim();
  const pass = document.getElementById("conn-dest-pass").value;
  const token = (document.getElementById("conn-dest-token").value || "").trim();

  const pill = document.getElementById("conn-dest-status-pill");
  pill.className = "pill run";
  pill.innerText = "Connecting...";

  await withButtonProgress(
    "btn-test-dest",
    "Authenticating...",
    "Connecting to Bold Reports Server",
    async () => {
      const res = await Api.testDestinationConnection(
        currentRun.id,
        url,
        mode === "credentials" ? email : null,
        mode === "credentials" ? pass : null,
        mode === "token" ? token : null
      );

      if (res.success) {
        currentRun.destinationServerUrl = res.destinationUrl || url;
        destinationSessionCache = {
          destinationUrl: res.destinationUrl || url,
          bearerToken: mode === "token" ? token : ""
        };
        pill.className = "pill ok";
        pill.innerText = "Token valid";
        showToast("Bold Reports connection verified. Ready for upload.");
      } else {
        pill.className = "pill fail";
        pill.innerText = "Failed";
        showToast(`Bold Reports authentication failed: ${res.message}`);
      }
    },
    `Authenticating with ${url}...`
  );
}


function resetLocalRunState() {
  reportsCache = [];
  selectedReportIds = new Set();
  rdlCache = {};
  dataSourcesCache = [];
  selectedValidationReportId = null;
  latestValidation = null;
}

async function applyFormConnectionsToCurrentRun() {
  const ssrsUrl = (document.getElementById("conn-ssrs-url")?.value || "").trim();
  const ssrsMode = document.getElementById("conn-ssrs-auth")?.value || "Integrated";
  const credMode = document.getElementById("conn-storage-mode")?.value || null;
  const ssrsUser = ssrsMode !== "Integrated" ? (document.getElementById("conn-ssrs-user")?.value || "") : "";
  const ssrsPass = ssrsMode !== "Integrated" ? (document.getElementById("conn-ssrs-pass")?.value || "") : "";

  const ssrsRes = await Api.testConnection(currentRun.id, ssrsUrl, ssrsUser, ssrsPass, ssrsMode, credMode);
  if (!ssrsRes || !ssrsRes.success) {
    throw new Error(ssrsRes?.message || "Could not connect to SSRS server.");
  }

  // Re-apply destination connection if the user already filled it in
  const destUrl = (document.getElementById("conn-dest-url")?.value || "").trim();
  const destMode = document.getElementById("conn-dest-auth-mode")?.value || "credentials";
  const destEmail = (document.getElementById("conn-dest-email")?.value || "").trim();
  const destPass = document.getElementById("conn-dest-pass")?.value || "";
  const destToken = (document.getElementById("conn-dest-token")?.value || "").trim();
  if (destUrl && ((destMode === "credentials" && destEmail && destPass) || (destMode === "token" && destToken))) {
    await Api.testDestinationConnection(
      currentRun.id,
      destUrl,
      destMode === "credentials" ? destEmail : null,
      destMode === "credentials" ? destPass : null,
      destMode === "token" ? destToken : null
    );
  }
}

async function saveAndDiscoverCatalog(forceRescan = false) {
  if (!currentRun) return;
  if (!validateRequiredInput("conn-ssrs-url", "Please enter your SSRS Report Server URL before discovering.")) return;
  const btn = document.getElementById("btn-discover-primary");
  const btnFooter = null;

  try {
    const existingPoll = await Api.pollState(currentRun.id);
    if (existingPoll && existingPoll.itemsTotal > 0) {
      const confirmed = await confirmRescanModal(existingPoll);
      if (!confirmed) return;

      beginActivity("discover", "Preparing Fresh Migration Run", "Connecting to SSRS & Bold Reports...", 5);
      // Start a brand-new run so old items, edits, and validation results are discarded cleanly
      const ssrsUrl = (document.getElementById("conn-ssrs-url")?.value || "").trim();
      await Api.abandonRun(currentRun.id);
      currentRun = await Api.createOrGetRun("SSRS Live Migration", ssrsUrl, true);
      resetLocalRunState();
      await applyFormConnectionsToCurrentRun();
    } else {
      const formSsrsUrl = (document.getElementById("conn-ssrs-url")?.value || "").trim();
      if (!currentRun.sourceServerUrl || currentRun.sourceServerUrl !== formSsrsUrl) {
        await applyFormConnectionsToCurrentRun();
      }
    }

    setButtonLoading(btn, true, "Discovering Catalog...");
    setButtonLoading(btnFooter, true, "Discovering Catalog...");
    beginActivity("discover", "Discovering SSRS Catalog", "Fetching folders, data sources, datasets, and reports...", 15);
    showToast("Scanning SSRS catalog: folders, data sources, datasets, and reports...");

    // 1. Trigger Fetch and poll until discovery populates items (up to ~18s)
    await Api.fetchEstate(currentRun.id);

    for (let i = 0; i < 30; i++) {
      await new Promise(r => setTimeout(r, 600));
      const poll = await Api.pollState(currentRun.id);
      if (poll) {
        updateJourneyBadges(poll);
        const pct = Math.min(55, 15 + Math.round((i / 30) * 40));
        updateActivity("discover", `Discovered ${poll.itemsTotal || 0} catalog items (${poll.reportsTotal || 0} reports)...`, poll.itemsTotal > 0 ? 55 : pct);
        if (poll.itemsTotal > 0 && poll.status !== "Fetching") {
          break;
        }
      }
    }

    // 2. Trigger Assessment check and poll until status reaches Analysed
    updateActivity("discover", "Evaluating RDL schemas, parameters, and compatibility rules...", 65, "Running Compatibility Assessment");
    await Api.checkEstate(currentRun.id);
    for (let i = 0; i < 20; i++) {
      await new Promise(r => setTimeout(r, 500));
      const poll = await Api.pollState(currentRun.id);
      if (poll) {
        updateJourneyBadges(poll);
        if (poll.status === "Analysed") {
          updateActivity("discover", `Analysed ${poll.reportsTotal || 0} reports.`, 90);
          break;
        }
      }
    }

    // 3. Refresh caches and stay on Step 1 so the user can enter discovered data source credentials
    await refreshConnectionsState();
    await refreshLibraryState();
    updateActivity("discover", "Catalog and data sources ready.", 100);

    const dsBannerNeedsAction = document.getElementById("s1-ds-summary-banner")?.classList.contains("needs-action");
    if (dataSourcesCache && dataSourcesCache.length > 0 && dsBannerNeedsAction) {
      showToast("Catalog discovered! Enter SQL data source credentials in the slide-over drawer.");
      openDataSourcesDrawer();
    } else {
      showToast("Catalog discovered and assessed! Ready for Step 2.");
      switchTab("assess");
    }
  } catch (err) {
    showToast("Discovery error: " + err.message);
  } finally {
    endActivity("discover");
    setButtonLoading(btn, false);
    setButtonLoading(btnFooter, false);
  }
}

function toggleBulkRemapPanel() {
  const panel = document.getElementById("bulk-remap-panel");
  const btn = document.getElementById("btn-toggle-bulk-remap");
  if (!panel) return;
  const open = panel.style.display === "none" || !panel.style.display;
  panel.style.display = open ? "block" : "none";
  if (btn) btn.setAttribute("aria-expanded", String(open));
}

async function runBulkRemap(dryRun) {
  if (!currentRun) return;
  const payload = {
    findServer: (document.getElementById("remap-find-server")?.value || "").trim() || null,
    replaceServer: (document.getElementById("remap-replace-server")?.value || "").trim() || null,
    findDatabase: (document.getElementById("remap-find-db")?.value || "").trim() || null,
    replaceDatabase: (document.getElementById("remap-replace-db")?.value || "").trim() || null,
    credentialRetrieval: (document.getElementById("remap-auth-mode")?.value || "").trim() || null,
    userName: (document.getElementById("remap-username")?.value || "").trim() || null,
    password: document.getElementById("remap-password")?.value || null,
    trustServerCertificate: !!document.getElementById("conn-ds-trust-cert")?.checked,
    dryRun: !!dryRun
  };

  const btnId = dryRun ? "btn-preview-remap" : "btn-apply-remap";
  const box = document.getElementById("remap-preview-box");

  await withButtonProgress(
    btnId,
    dryRun ? "Previewing..." : "Applying...",
    dryRun ? "Previewing Bulk Data Source Remap" : "Applying Bulk Data Source Remap",
    async () => {
      try {
        const res = await Api.bulkRemapDataSources(currentRun.id, payload);
        if (!res.success) {
          showToast(res.message || "Bulk remap failed.");
          return;
        }
        const items = res.items || [];
        if (box) {
          box.style.display = "block";
          box.innerHTML = items.length === 0
            ? `<span style="font-size:12px;color:var(--br-text-muted);">No staged data sources to remap yet. Discover catalog first.</span>`
            : `
              <div style="font-size:12px;font-weight:600;margin-bottom:6px;color:var(--br-text);">
                ${dryRun ? `Preview: ${res.updatedCount} of ${res.total} data source(s) will be updated` : `Applied changes to ${res.updatedCount} of ${res.total} data source(s)`}
              </div>
              <div class="table-scroll" style="max-height:190px;">
                <table>
                  <thead><tr><th>Data Source</th><th>Before</th><th>After</th><th>Changed</th></tr></thead>
                  <tbody>
                    ${items.map(i => `
                      <tr>
                        <td><strong>${escapeHtml(i.name)}</strong></td>
                        <td style="font-family:var(--br-font-mono);font-size:11.5px;">${escapeHtml(i.before || "(empty)")}</td>
                        <td style="font-family:var(--br-font-mono);font-size:11.5px;">${escapeHtml(i.after || "(empty)")}</td>
                        <td><span class="pill ${i.changed ? "ok" : ""}" style="font-size:11px;">${i.changed ? "Yes" : "No"}</span></td>
                      </tr>`).join("")}
                  </tbody>
                </table>
              </div>`;
        }
        showToast(dryRun
          ? `Remap preview ready (${res.updatedCount} change(s)).`
          : `Updated ${res.updatedCount} data source(s) in staging.`);
        if (!dryRun) {
          await refreshConnectionsState();
        }
      } catch (err) {
        showToast("Bulk remap error: " + err.message);
      }
    }
  );
}

// -----------------------------------------------------------------------------
// Step 2: Report Library (with Multi-Select & Direct Migration Trigger)
// -----------------------------------------------------------------------------
