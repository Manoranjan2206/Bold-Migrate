// Standalone "Bulk Delete" tool for reports, datasets, data sources, and folders.
// Credentials are sent per request and never stored.
let bdItems = [];
let bdTypeFilter = "all";
const bdSelected = new Set();

function bdCreds() {
  return {
    url: document.getElementById("bd-url").value.trim(),
    email: document.getElementById("bd-email").value.trim(),
    password: document.getElementById("bd-password").value
  };
}

function bdSetStatus(msg) {
  document.getElementById("bd-status").textContent = msg || "";
}

async function bdPost(path, body) {
  const csrf = typeof Api !== "undefined" && Api.getCsrfToken ? Api.getCsrfToken() : "";
  const headers = { "Content-Type": "application/json" };
  if (csrf) headers["X-BoldMigrate-Csrf"] = csrf;
  const res = await fetch(`/api/tools/bold-delete/${path}`, {
    method: "POST",
    headers,
    body: JSON.stringify(body)
  });
  return await res.json().catch(() => ({ success: false, message: `HTTP ${res.status}` }));
}

async function bdLoadReports() {
  const btn = document.getElementById("btn-bd-load");
  const runLoad = async () => {
    bdSetStatus("Loading reports, datasets, data sources, and folders...");
    const res = await bdPost("list", bdCreds());
    if (!res.success) {
      bdSetStatus(res.message || "Failed to load items.");
      return;
    }
    bdItems = (res.items || res.reports || []).map(i => ({
      id: i.id,
      name: i.name || "",
      category: i.category || "",
      itemType: i.itemType || "Report"
    }));
    bdSelected.clear();
    bdUpdateCounts();
    bdSetStatus(`${bdItems.length} item(s) loaded.`);
    bdRender();
  };
  if (typeof withButtonProgress === "function") {
    return await withButtonProgress(btn, "Loading...", "Loading Items from Bold Reports", runLoad);
  }
  return await runLoad();
}

function bdSetTypeFilter(type, el) {
  bdTypeFilter = type || "all";
  document.querySelectorAll("#bd-type-chips .chip").forEach(c => c.classList.remove("active"));
  if (el) el.classList.add("active");
  bdRender();
}

function bdUpdateCounts() {
  const counts = { all: bdItems.length, Report: 0, Dataset: 0, Datasource: 0, Folder: 0 };
  for (const i of bdItems) {
    if (counts[i.itemType] !== undefined) counts[i.itemType]++;
  }
  const set = (id, val) => { const el = document.getElementById(id); if (el) el.textContent = val; };
  set("bd-count-all", counts.all);
  set("bd-count-report", counts.Report);
  set("bd-count-dataset", counts.Dataset);
  set("bd-count-datasource", counts.Datasource);
  set("bd-count-folder", counts.Folder);
}

function bdVisible() {
  const q = document.getElementById("bd-filter").value.trim().toLowerCase();
  return bdItems.filter(r => {
    if (bdTypeFilter !== "all" && r.itemType !== bdTypeFilter) return false;
    if (!q) return true;
    return r.name.toLowerCase().includes(q) ||
      r.category.toLowerCase().includes(q) ||
      r.itemType.toLowerCase().includes(q);
  });
}

function bdTypeBadge(type) {
  const labels = { Report: "Report", Dataset: "Dataset", Datasource: "Data Source", Folder: "Folder" };
  const cls = type === "Report" ? "ok" : type === "Folder" ? "warn" : "";
  return `<span class="pill ${cls}" style="font-size:11px;">${labels[type] || escapeHtml(type)}</span>`;
}

function bdRender() {
  const rows = bdVisible();
  const body = document.getElementById("bd-body");
  body.innerHTML = rows.length
    ? rows.map(r => `<tr>
        <td><input type="checkbox" class="bd-row-check" data-bd-id="${escapeHtml(r.id)}" ${bdSelected.has(r.id) ? "checked" : ""}></td>
        <td>${escapeHtml(r.name)}</td>
        <td>${bdTypeBadge(r.itemType)}</td>
        <td>${escapeHtml(r.category || (r.itemType === "Folder" ? "(Root)" : ""))}</td></tr>`).join("")
    : `<tr><td colspan="4" style="color:var(--br-text-subtle);padding:12px;">No matching items.</td></tr>`;

  const selectAll = document.getElementById("bd-select-all");
  if (selectAll) {
    selectAll.checked = rows.length > 0 && rows.every(r => bdSelected.has(r.id));
  }

  if (!body.dataset.bdBound) {
    body.dataset.bdBound = "1";
    body.addEventListener("change", (ev) => {
      const target = ev.target;
      if (target && target.classList.contains("bd-row-check")) {
        bdToggle(target.dataset.bdId, target.checked);
      }
    });
  }
  bdUpdateButton();
}

function bdToggle(id, checked) {
  if (checked) bdSelected.add(id); else bdSelected.delete(id);
  bdUpdateButton();
}

function bdSelectVisible(checked) {
  bdVisible().forEach(r => checked ? bdSelected.add(r.id) : bdSelected.delete(r.id));
  bdRender();
}

function bdUpdateButton() {
  const btn = document.getElementById("bd-delete-btn");
  btn.textContent = `Delete Selected (${bdSelected.size})`;
  btn.disabled = bdSelected.size === 0;
}

async function bdDeleteSelected() {
  const n = bdSelected.size;
  if (!n) return;
  const selectedItems = bdItems.filter(i => bdSelected.has(i.id));
  const summaryParts = [];
  const typeOrder = ["Report", "Dataset", "Datasource", "Folder"];
  for (const t of typeOrder) {
    const c = selectedItems.filter(i => i.itemType === t).length;
    if (c > 0) summaryParts.push(`${c} ${t === "Datasource" ? "Data Source" : t}(s)`);
  }
  const msg = `Permanently delete ${n} item(s) (${summaryParts.join(", ")}) from Bold Reports?\n\nItems are deleted in dependency order (Reports → Datasets → Data Sources → Folders). This cannot be undone.`;
  const confirmed = typeof showConfirmDialog === "function"
    ? await showConfirmDialog({
        title: "Delete Items in Bold Reports",
        message: msg,
        confirmText: "Permanently Delete",
        danger: true
      })
    : confirm(msg);
  if (!confirmed) return;
  const btn = document.getElementById("bd-delete-btn");
  const runDelete = async () => {
    bdSetStatus(`Deleting ${n} item(s)...`);
    const res = await bdPost("delete", {
      ...bdCreds(),
      items: selectedItems.map(i => ({ id: i.id, itemType: i.itemType }))
    });
    if (res.deletedIds) {
      const gone = new Set(res.deletedIds);
      bdItems = bdItems.filter(r => !gone.has(r.id));
      gone.forEach(id => bdSelected.delete(id));
      bdUpdateCounts();
      bdRender();
    }
    bdSetStatus(res.message || "Done.");
    if (typeof showToast === "function") showToast(res.message || "Done.");
  };
  if (typeof withButtonProgress === "function") {
    return await withButtonProgress(btn, "Deleting...", `Deleting ${n} item(s) from Bold Reports`, runDelete);
  }
  return await runDelete();
}
