// Step 4: local preview & edit of staged reports.
//
// Every converted report is served from local staging by the Bold Migrate server itself
// (api/preview/viewer, api/preview/designer), so it can be viewed and edited before it is
// uploaded. Shared data sources and datasets resolve inside the staged SSRS folder tree.
// Designer saves go back into staging and are sent to Bold Reports by the upload step.

const Preview = {
  data: null,
  filter: "",
  open: null, // { mode: "viewer" | "designer", reportId }
  // Saving inside an open designer marks the report edited; refresh the list when it closes.
  dirty: false,

  async render(grid) {
    grid.innerHTML = `
      <div class="card" style="grid-column: 1 / -1; padding: 20px;">
        <p class="sub">Loading staged reports…</p>
      </div>`;
    try {
      this.data = await Api.getPreviewReports(currentRun.id);
    } catch (err) {
      grid.innerHTML = `
        <div class="card" style="grid-column: 1 / -1; padding: 20px;">
          <h2>Staged Reports</h2>
          <p class="sub" style="color:var(--br-danger);">${escapeHtml(err.message)}</p>
        </div>`;
      return;
    }

    if (this.open) {
      const report = this.data.reports.find(r => r.id === this.open.reportId);
      if (report) {
        this.renderHost(grid, report, this.open.mode);
        return;
      }
      this.open = null;
    }
    this.renderList(grid);
  },

  renderList(grid) {
    const reports = this.data.reports;
    const edited = reports.filter(r => r.localEditPendingUpload).length;

    grid.innerHTML = `
      <div class="card" style="grid-column: 1 / -1; padding: 20px;">
        <div style="display:flex;justify-content:space-between;align-items:flex-start;gap:16px;flex-wrap:wrap;margin-bottom:14px;">
          <div>
            <h2>Staged Reports — Local Preview &amp; Edit</h2>
            <p class="sub" style="max-width:760px;">
              Converted reports run from local staging, before they are uploaded. Shared data sources and
              datasets resolve inside the staged SSRS folder tree. Credentials entered in Step 1 are used;
              any data source without them asks for credentials when the report runs.
              Saving in the designer updates the staged report, and the next upload sends it to Bold Reports.
            </p>
          </div>
          <div style="display:flex;gap:10px;align-items:center;">
            ${edited ? `<span class="pill warn">${edited} edited, pending upload</span>` : ""}
            <label class="sr-only" for="preview-filter">Filter staged reports</label>
            <input id="preview-filter" type="search" placeholder="Filter by name or folder" value="${escapeHtml(this.filter)}"
                   oninput="Preview.setFilter(this.value)"
                   style="height:34px;width:240px;padding:0 10px;border:1px solid var(--br-border-strong);border-radius:6px;">
          </div>
        </div>
        <table>
          <thead>
            <tr>
              <th>Report</th>
              <th>Folder</th>
              <th>Data sources</th>
              <th>Shared datasets</th>
              <th>Status</th>
              <th style="text-align:right;">Open</th>
            </tr>
          </thead>
          <tbody id="preview-report-rows">${this.rowsHtml()}</tbody>
        </table>
      </div>`;
  },

  rowsHtml() {
    const term = this.filter.trim().toLowerCase();
    const rows = this.data.reports.filter(r =>
      !term || r.name.toLowerCase().includes(term) || r.folder.toLowerCase().includes(term));

    if (this.data.reports.length === 0) {
      return `<tr><td colspan="6" style="text-align:center;color:var(--br-text-muted);padding:20px;">
        No reports are staged yet. Run Step 1: Connect &amp; Discover to download them from SSRS.</td></tr>`;
    }
    if (rows.length === 0) {
      return `<tr><td colspan="6" style="text-align:center;color:var(--br-text-muted);padding:20px;">No reports match the filter.</td></tr>`;
    }

    return rows.map(r => {
      const disabled = r.staged ? "" : "disabled title=\"This report's definition is not in staging\"";
      return `
        <tr>
          <td><strong>${escapeHtml(r.name)}</strong></td>
          <td><code>${escapeHtml(r.folder)}</code></td>
          <td>${this.dataSourcesHtml(r)}</td>
          <td>${this.dataSetsHtml(r)}</td>
          <td>${this.statusHtml(r)}</td>
          <td style="text-align:right;white-space:nowrap;">
            <button class="btn2 btn-sm" ${disabled} onclick="Preview.openReport('${r.id}', 'viewer')">Viewer</button>
            <button class="btn btn-sm" ${disabled} onclick="Preview.openReport('${r.id}', 'designer')">Designer</button>
          </td>
        </tr>`;
    }).join("");
  },

  dataSourcesHtml(r) {
    if (!r.dataSources.length) return `<span style="color:var(--br-text-subtle);">—</span>`;
    const label = {
      InReport: ["Embedded", "ok"],
      Stored: ["Credentials stored", "ok"],
      Integrated: ["Windows integrated", "ok"],
      None: ["No credentials", "ok"],
      PromptAtRunTime: ["Asks at run time", "warn"],
      Unresolved: ["Not found in staging", "fail"]
    };
    return r.dataSources.map(ds => {
      const [text, cls] = label[ds.credentials] || [ds.credentials, ""];
      const target = ds.kind === "Shared" ? (ds.resolvedPath || ds.reference) : "embedded";
      return `<div style="margin:2px 0;" title="${escapeHtml(target)}">
        ${escapeHtml(ds.name)} <span class="pill ${cls}" style="font-size:11px;">${escapeHtml(text)}</span></div>`;
    }).join("");
  },

  dataSetsHtml(r) {
    if (!r.dataSets.length) return `<span style="color:var(--br-text-subtle);">—</span>`;
    return r.dataSets.map(ds => `<div style="margin:2px 0;" title="${escapeHtml(ds.resolvedPath || ds.reference)}">
      ${escapeHtml(ds.name)} ${ds.resolved ? "" : `<span class="pill fail" style="font-size:11px;">Not found in staging</span>`}</div>`).join("");
  },

  statusHtml(r) {
    const pills = [];
    if (!r.staged) pills.push(`<span class="pill fail">Not staged</span>`);
    if (r.localEditPendingUpload) {
      pills.push(`<span class="pill warn" title="Edited ${escapeHtml(new Date(r.localEditUtc).toLocaleString())}">Edited · upload pending</span>`);
    } else if (r.localEditUtc) {
      pills.push(`<span class="pill ok">Edit uploaded</span>`);
    }
    pills.push(`<span class="pill ${r.uploaded ? "ok" : ""}">${r.uploaded ? "On Bold Reports" : "Not uploaded"}</span>`);
    return pills.join(" ");
  },

  setFilter(value) {
    this.filter = value;
    const body = document.getElementById("preview-report-rows");
    if (body) body.innerHTML = this.rowsHtml();
  },

  openReport(reportId, mode) {
    this.open = { mode, reportId };
    this.dirty = false;
    const report = this.data.reports.find(r => r.id === reportId);
    const grid = document.getElementById("val-viewport-grid");
    if (report && grid) this.renderHost(grid, report, mode);
  },

  async close() {
    this.destroyWidgets();
    this.open = null;
    const grid = document.getElementById("val-viewport-grid");
    if (!grid) return;
    if (this.dirty) {
      this.dirty = false;
      await this.render(grid); // reload so edit status is current
    } else {
      this.renderList(grid);
    }
  },

  renderHost(grid, report, mode) {
    this.destroyWidgets();
    const isDesigner = mode === "designer";
    const other = isDesigner ? "viewer" : "designer";

    grid.innerHTML = `
      <div class="card" style="grid-column: 1 / -1; padding: 0; overflow: hidden; display: flex; flex-direction: column; height: 800px;">
        <div style="background:var(--br-navy);color:var(--br-text-inverse);padding:10px 16px;display:flex;justify-content:space-between;align-items:center;gap:12px;flex-wrap:wrap;">
          <div style="display:flex;align-items:center;gap:12px;min-width:0;">
            <span style="font-size:15px;font-weight:700;color:var(--br-log-stage);">${isDesigner ? "Report Designer" : "Report Viewer"} · local staging</span>
            <span class="pill ok" style="font-size:11.5px;">${escapeHtml(report.name)}</span>
            <code style="font-size:11.5px;color:var(--br-text-subtle);overflow:hidden;text-overflow:ellipsis;white-space:nowrap;">${escapeHtml(report.path)}</code>
          </div>
          <div style="display:flex;gap:10px;">
            <button class="btn2 btn-sm" onclick="Preview.openReport('${report.id}', '${other}')">Open in ${isDesigner ? "Viewer" : "Designer"}</button>
            <button class="btn2 btn-sm" onclick="Preview.close()">Back to staged reports</button>
          </div>
        </div>
        ${isDesigner ? `<div style="background:var(--br-warning-bg);color:var(--br-warning-text);font-size:12.5px;padding:6px 16px;">
          Save writes the report back into staging. It is sent to Bold Reports on the next upload (Step 5) — SSRS is not contacted again.</div>` : ""}
        <div id="preview-widget" style="flex:1;width:100%;min-height:680px;"></div>
      </div>`;

    if (!window.jQuery || typeof window.jQuery.fn.boldReportViewer !== "function") {
      document.getElementById("preview-widget").innerHTML = `
        <p class="sub" style="padding:24px;color:var(--br-danger);">The Bold Reports scripts did not load. Check network access to cdn.boldreports.com and reload.</p>`;
      return;
    }

    const $host = window.jQuery("#preview-widget");
    if (isDesigner) {
      $host.boldReportDesigner({
        serviceUrl: this.data.designerServiceUrl,
        // Open the staged report once the designer has initialised. The path is resolved by the
        // run-scoped staged server, so no report server URL is involved.
        create: () => {
          const designer = $host.data("boldReportDesigner");
          if (designer) designer.openReport(report.path);
        },
        // New/Open would leave the staged run: only the opened report can be saved.
        toolbarSettings: { items: ej.ReportDesigner.ToolbarItems.All & ~ej.ReportDesigner.ToolbarItems.New & ~ej.ReportDesigner.ToolbarItems.Open },
        reportSaved: () => {
          this.dirty = true;
          showToast(`'${report.name}' saved to staging. It will be uploaded to Bold Reports in the next upload.`);
        }
      });
    } else {
      // The service URLs carry the run id in their path, so every widget request is run-scoped.
      $host.boldReportViewer({
        reportServiceUrl: this.data.viewerServiceUrl,
        reportPath: report.path
      });
    }
  },

  destroyWidgets() {
    const $host = window.jQuery ? window.jQuery("#preview-widget") : null;
    if (!$host || !$host.length) return;
    for (const name of ["boldReportDesigner", "boldReportViewer"]) {
      if ($host.data(name)) {
        try { $host[name]("destroy"); } catch { /* widget already torn down */ }
      }
    }
  }
};
