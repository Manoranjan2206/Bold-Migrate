// Bold Migrate — Step 5 & 6: Batch Migration Pipeline & Cutover

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
      const startBtn = document.getElementById("btn-start-migration-tab");
      if (poll.status === "Uploading") {
        setButtonLoading(startBtn, true, `Uploading (${repsUploaded}/${repsTotal})...`);
      } else {
        setButtonLoading(startBtn, false);
        if (startBtn) startBtn.innerText = repsTotal > 0 && repsUploaded === repsTotal ? "Re-run Upload Pipeline" : "Start Upload Pipeline";
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
  const conflictPolicy = document.getElementById("upload-conflict-policy")?.value || "Skip";
  setButtonLoading(btn, true, "Starting Upload...");
  beginActivity("upload-pipeline", "Batch Upload Pipeline Running", `Uploading with '${conflictPolicy}' conflict policy...`, 5);
  showToast(`Starting 4-stage dependency upload (${conflictPolicy} on conflict)...`);
  try {
    await Api.startUpload(currentRun.id, null, conflictPolicy);
    showToast("Upload pipeline actively executing.");
    await refreshMigrationState();
  } catch (err) {
    endActivity("upload-pipeline");
    setButtonLoading(btn, false);
    showToast("Upload error: " + err.message);
  }
}

async function runUploadDryRun() {
  if (!currentRun) return;
  const box = document.getElementById("upload-dryrun-box");
  const conflictPolicy = document.getElementById("upload-conflict-policy")?.value || "Skip";

  await withButtonProgress(
    "btn-upload-dryrun",
    "Scanning Destination...",
    "Running Upload Conflict Dry-Run",
    async () => {
      try {
        const plan = await Api.dryRunUpload(currentRun.id, conflictPolicy);
        if (!box) return;
        box.style.display = "block";
        if (plan.message && !plan.items) {
          box.innerHTML = `<span style="color:var(--br-danger-text);font-size:12.5px;">${escapeHtml(plan.message)}</span>`;
          return;
        }
        const items = plan.items || [];
        box.innerHTML = `
          <div style="display:flex;gap:8px;flex-wrap:wrap;margin-bottom:10px;">
            <span class="pill ok">Create: ${plan.createCount || 0}</span>
            <span class="pill">Skip: ${plan.skipCount || 0}</span>
            <span class="pill warn">Overwrite: ${plan.overwriteCount || 0}</span>
            <span class="pill warn">Rename: ${plan.renameCount || 0}</span>
            <span class="pill ${plan.blockedCount > 0 ? "fail" : ""}">Blocked / Not Ready: ${plan.blockedCount || 0}</span>
          </div>
          ${items.length === 0
            ? `<div style="font-size:12.5px;color:var(--br-text-muted);">No staged items in this run.</div>`
            : `<div class="table-scroll" style="max-height:220px;">
                <table>
                  <thead><tr><th>Stage</th><th>Type</th><th>Source Name</th><th>Planned Action</th><th>Destination Name</th><th>Note</th></tr></thead>
                  <tbody>
                    ${items.map(p => {
                      const cls = p.action === "Create" ? "ok" : p.action === "Blocked" ? "fail" : (p.action === "Overwrite" || p.action === "Rename") ? "warn" : "";
                      return `
                        <tr>
                          <td>${p.stage}</td>
                          <td>${escapeHtml(p.itemType)}</td>
                          <td><strong>${escapeHtml(p.sourceName)}</strong></td>
                          <td><span class="pill ${cls}" style="font-size:11px;">${escapeHtml(p.action)}</span></td>
                          <td style="font-family:var(--br-font-mono);font-size:11.5px;">${escapeHtml(p.targetName)}</td>
                          <td style="font-size:11.5px;color:var(--br-text-muted);">${escapeHtml(p.note || "")}</td>
                        </tr>`;
                    }).join("")}
                  </tbody>
                </table>
              </div>`}`;
      } catch (err) {
        if (box) {
          box.style.display = "block";
          box.innerHTML = `<span style="color:var(--br-danger-text);font-size:12.5px;">Dry-run failed: ${escapeHtml(err.message)}</span>`;
        }
        showToast("Dry-run failed: " + err.message);
      }
    },
    `Comparing local items against Bold Reports destination (${conflictPolicy})...`
  );
}

async function retryFailedItems() {
  if (!currentRun) return;
  await withButtonProgress(
    "btn-retry-failed",
    "Retrying...",
    "Retrying Failed Pipeline Items",
    async () => {
      await Api.retryFailed(currentRun.id);
      await refreshMigrationState();
      showToast("Retry triggered for failed items.");
    },
    "Resetting failed upload/validation states and re-queuing..."
  );
}

async function togglePauseMigration() {
  if (!currentRun) return;
  const btn = document.getElementById("btn-pause-migration");
  if (!isPaused) {
    await withButtonProgress(btn, "Pausing...", "Pausing Upload Pipeline", async () => {
      await Api.startUpload(currentRun.id, "pause");
      isPaused = true;
      showToast("Upload execution paused.");
    });
    if (btn) btn.innerText = "Resume";
  } else {
    await withButtonProgress(btn, "Resuming...", "Resuming Upload Pipeline", async () => {
      await Api.startUpload(currentRun.id, "resume");
      isPaused = false;
      showToast("Upload execution resumed.");
    });
    if (btn) btn.innerText = "Pause";
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

    const ready = !!(gatesRes && gatesRes.ready);
    document.querySelectorAll(".confirm-cutover-btn").forEach(btn => {
      btn.disabled = !ready;
      btn.style.opacity = ready ? "1" : "0.55";
      btn.title = ready ? "Confirm production cutover" : "Upload and validate all selected reports before cutover";
    });

    const shimGate = (gatesRes?.gates || []).find(g => g.name === "URL compatibility shim configured");
    const shimToggle = document.getElementById("setting-shim-enabled");
    if (shimToggle && shimGate) {
      shimToggle.checked = !!shimGate.passed;
    }
  } catch { }
}

async function toggleCutoverShim(enabled) {
  beginActivity("toggle-shim", "Updating URL Compatibility Shim", enabled ? "Enabling SSRS URL redirection..." : "Disabling SSRS URL redirection...", null);
  try {
    const current = await Api.getSettings();
    current["UrlShim.Enabled"] = !!enabled;
    await Api.updateSettings(current);
    showToast(`URL Compatibility Shim ${enabled ? "enabled" : "disabled"}.`);
    await refreshCutoverState();
  } catch (err) {
    showToast("Failed updating shim setting: " + err.message);
  } finally {
    endActivity("toggle-shim");
  }
}

async function runDeltaSyncAction() {
  if (!currentRun) return;
  await withButtonProgress(
    "btn-delta-sync",
    "Syncing Delta...",
    "Running Incremental Delta Sync",
    async () => {
      try {
        const res = await Api.deltaSync(currentRun.id);
        showToast(`Delta sync finished. Modified: ${res?.modifiedCount ?? 0}, Added: ${res?.addedCount ?? 0}.`);
        await refreshLibraryState();
        await refreshCutoverState();
      } catch (err) {
        showToast("Delta sync failed: " + err.message);
      }
    },
    "Checking SSRS for modified items since last upload..."
  );
}

async function handleConfirmCutover() {
  if (!currentRun) return;
  beginActivity("cutover-check", "Verifying Cutover Gates", "Checking pre-flight migration readiness...", null);
  try {
    const gatesRes = await Api.getCutoverGates(currentRun.id);
    endActivity("cutover-check");

    if (!gatesRes || !gatesRes.ready) {
      await showAlertDialog({
        title: "Cutover Blocked",
        message: "No reports have been uploaded to Bold Reports Server yet.\n\nPlease connect to Bold Reports in Step 1 and execute Step 5: Upload Pipeline first.",
        danger: true
      });
      return;
    }

    const confirmed = await showConfirmDialog({
      title: "Finalize Production Cutover",
      message: "Are you ready to finalize production cutover? This will configure URL shims and activate Bold Reports as primary.",
      confirmText: "Confirm Cutover"
    });
    if (!confirmed) return;

    beginActivity("cutover-confirm", "Finalizing Production Cutover", "Activating Bold Reports and archiving credentials...", null);
    const res = await Api.confirmCutover(currentRun.id);
    if (res.success) {
      showToast("Production cutover completed! Bold Reports is now the primary reporting server.");
      await refreshCutoverState();
    } else {
      await showAlertDialog({ title: "Cutover Failed", message: res.message || "Cutover failed.", danger: true });
    }
  } catch (err) {
    await showAlertDialog({ title: "Cutover Failed", message: err.message || "Cutover failed.", danger: true });
  } finally {
    endActivity("cutover-check");
    endActivity("cutover-confirm");
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
// Standalone Tools: Local Folder Import
// -----------------------------------------------------------------------------
