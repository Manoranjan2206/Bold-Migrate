// Standalone "Bulk Delete Reports" tool. Credentials are sent per request and never stored.
let bdReports = [];
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
  const res = await fetch(`/api/tools/bold-delete/${path}`, {
    method: "POST",
    headers: { "Content-Type": "application/json" },
    body: JSON.stringify(body)
  });
  return await res.json().catch(() => ({ success: false, message: `HTTP ${res.status}` }));
}

async function bdLoadReports() {
  bdSetStatus("Loading...");
  const res = await bdPost("list", bdCreds());
  if (!res.success) {
    bdSetStatus(res.message || "Failed to load reports.");
    return;
  }
  bdReports = res.reports || [];
  bdSelected.clear();
  bdSetStatus(`${bdReports.length} report(s) loaded.`);
  bdRender();
}

function bdVisible() {
  const q = document.getElementById("bd-filter").value.trim().toLowerCase();
  return bdReports.filter(r => !q || r.name.toLowerCase().includes(q) || r.category.toLowerCase().includes(q));
}

function bdRender() {
  const rows = bdVisible();
  const body = document.getElementById("bd-body");
  body.innerHTML = rows.length
    ? rows.map(r => `<tr>
        <td><input type="checkbox" ${bdSelected.has(r.id) ? "checked" : ""} onchange="bdToggle('${escapeHtml(r.id)}', this.checked)"></td>
        <td>${escapeHtml(r.name)}</td><td>${escapeHtml(r.category)}</td></tr>`).join("")
    : `<tr><td colspan="3" style="color:var(--br-text-subtle);padding:12px;">No reports.</td></tr>`;
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
  if (!n || !confirm(`Permanently delete ${n} report(s) from Bold Reports?\n\nThis cannot be undone.`)) return;
  bdSetStatus("Deleting...");
  const res = await bdPost("delete", { ...bdCreds(), reportIds: Array.from(bdSelected) });
  if (res.deletedIds) {
    const gone = new Set(res.deletedIds);
    bdReports = bdReports.filter(r => !gone.has(r.id));
    gone.forEach(id => bdSelected.delete(id));
    bdRender();
  }
  bdSetStatus(res.message || "Done.");
  if (typeof showToast === "function") showToast(res.message || "Done.");
}
