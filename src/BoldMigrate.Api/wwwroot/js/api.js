// Bold Migrate API client

const Api = {
  baseUrl: "/api/migration",

  getCsrfToken() {
    const match = document.cookie.match(/(?:^|;\s*)bm_csrf=([^;]+)/);
    return match ? decodeURIComponent(match[1]) : "";
  },

  csrfHeaders(extra = {}) {
    const csrf = this.getCsrfToken();
    return csrf ? { ...extra, "X-BoldMigrate-Csrf": csrf } : { ...extra };
  },

  async request(url, options = {}) {
    const method = (options.method || "GET").toUpperCase();
    const isMutating = method !== "GET" && method !== "HEAD" && method !== "OPTIONS";
    const headers = isMutating ? this.csrfHeaders(options.headers || {}) : (options.headers || {});
    return await fetch(url, { ...options, method, headers });
  },

  async getActiveRun() {
    try {
      const res = await this.request(`${this.baseUrl}/active-run`);
      if (res.ok) return await res.json();
      return null;
    } catch {
      return null;
    }
  },

  async createOrGetRun(name, sourceUrl, forceNew = false) {
    const res = await this.request(`${this.baseUrl}/runs`, {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify({ name, sourceUrl, forceNew })
    });
    return await res.json();
  },

  async setCredentialMode(runId, credentialMode) {
    const res = await this.request(`${this.baseUrl}/runs/${runId}/credential-mode`, {
      method: "PUT",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify({ credentialMode })
    });
    return await res.json();
  },

  async testConnection(runId, sourceUrl, username, password, authMode = "Integrated", credentialMode = null) {
    const res = await this.request(`${this.baseUrl}/runs/${runId}/connect`, {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify({ sourceUrl, username, password, authMode, credentialMode })
    });
    return await res.json();
  },

  async testDestinationConnection(runId, destinationUrl, email, password, bearerToken) {
    const res = await this.request(`${this.baseUrl}/runs/${runId}/connect-destination`, {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify({ destinationUrl, email, password, bearerToken })
    });
    return await res.json();
  },

  async fetchEstate(runId) {
    const res = await this.request(`${this.baseUrl}/runs/${runId}/fetch`, { method: "POST" });
    return await res.json();
  },

  async checkEstate(runId) {
    const res = await this.request(`${this.baseUrl}/runs/${runId}/check`, { method: "POST" });
    return await res.json();
  },

  async startUpload(runId, action, conflictPolicy) {
    const params = [];
    if (action) params.push(`action=${encodeURIComponent(action)}`);
    if (conflictPolicy) params.push(`conflictPolicy=${encodeURIComponent(conflictPolicy)}`);
    const qs = params.length > 0 ? `?${params.join("&")}` : "";
    const res = await this.request(`${this.baseUrl}/runs/${runId}/upload${qs}`, { method: "POST" });
    return await res.json();
  },

  async startValidation(runId) {
    const res = await this.request(`${this.baseUrl}/runs/${runId}/validate`, { method: "POST" });
    return await res.json();
  },

  async validateRender(runId, concurrency = 5, reportIds = null) {
    const res = await this.request(`${this.baseUrl}/runs/${runId}/validate-render?concurrency=${concurrency}`, {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify({ reportIds: reportIds && reportIds.length > 0 ? reportIds : null })
    });
    return await res.json();
  },

  async skipValidation(runId) {
    const res = await this.request(`${this.baseUrl}/runs/${runId}/skip-validation`, { method: "POST" });
    return await res.json();
  },

  async clearValidationResults(runId) {
    const res = await this.request(`${this.baseUrl}/runs/${runId}/validation-results`, { method: "DELETE" });
    return await res.json();
  },

  async getValidationSummary(runId) {
    const res = await this.request(`${this.baseUrl}/runs/${runId}/validation-summary`);
    if (!res.ok) throw new Error(`Failed to load validation summary (HTTP ${res.status})`);
    return await res.json();
  },

  async getRenderLogs(runId, reportPath) {
    const q = reportPath ? `?reportPath=${encodeURIComponent(reportPath)}` : "";
    const res = await this.request(`/api/preview/runs/${runId}/viewer/render-logs${q}`);
    if (!res.ok) return [];
    return await res.json();
  },

  async retryFailed(runId) {
    const res = await this.request(`${this.baseUrl}/runs/${runId}/retry`, { method: "POST" });
    return await res.json();
  },

  async abandonRun(runId) {
    const res = await this.request(`${this.baseUrl}/runs/${runId}/abandon`, { method: "POST" });
    return await res.json();
  },

  async getEstate(runId) {
    const res = await this.request(`${this.baseUrl}/runs/${runId}/estate`);
    return await res.json();
  },

  async getDataSources(runId) {
    try {
      const res = await this.request(`${this.baseUrl}/runs/${runId}/datasources`);
      if (res.ok) return await res.json();
      return [];
    } catch {
      return [];
    }
  },

  async getReports(runId) {
    const res = await this.request(`${this.baseUrl}/runs/${runId}/reports`);
    return await res.json();
  },

  async selectReports(runId, selectedReportIds) {
    const res = await this.request(`${this.baseUrl}/runs/${runId}/select-reports`, {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify({ selectedReportIds })
    });
    return await res.json();
  },

  // Returns the measured source-vs-destination comparison for one report, or
  // { validated: false } when validation has not produced a result yet.
  async getReportValidation(runId, reportId) {
    const res = await this.request(`${this.baseUrl}/runs/${runId}/reports/${reportId}/validation`);
    if (!res.ok) return { validated: false, message: `Request failed (HTTP ${res.status})` };
    return await res.json();
  },

  async getSettings() {
    const res = await this.request("/api/settings");
    if (!res.ok) throw new Error(`Failed to load settings (HTTP ${res.status})`);
    return await res.json();
  },

  async getRules() {
    const res = await this.request("/api/settings/rules");
    if (!res.ok) throw new Error(`Failed to load compatibility rules (HTTP ${res.status})`);
    return await res.json();
  },

  async updateSettings(settings) {
    const res = await this.request("/api/settings", {
      method: "PUT",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify(settings)
    });
    if (!res.ok) {
      const data = await res.json().catch(() => null);
      throw new Error(data?.message || `Failed to save settings (HTTP ${res.status})`);
    }
    return await res.json();
  },

  async scanFile(formData) {
    const res = await this.request("/api/scanner/analyse", { method: "POST", body: formData });
    const data = await res.json().catch(() => null);
    if (!res.ok) throw new Error(data?.message || `Scan failed (HTTP ${res.status})`);
    return data;
  },

  async getReportRdl(runId, reportId) {
    const res = await this.request(`${this.baseUrl}/runs/${runId}/reports/${reportId}/rdl`);
    return await res.json();
  },

  async getReviewQueue(runId) {
    const res = await this.request(`${this.baseUrl}/runs/${runId}/review-queue`);
    return await res.json();
  },

  async approveReport(runId, reportId, comment) {
    const res = await this.request(`${this.baseUrl}/runs/${runId}/reports/${reportId}/approve`, {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify({ comment })
    });
    return await res.json();
  },

  async flagReport(runId, reportId, comment) {
    const res = await this.request(`${this.baseUrl}/runs/${runId}/reports/${reportId}/flag`, {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify({ comment })
    });
    return await res.json();
  },

  async bulkApprove(runId) {
    const res = await this.request(`${this.baseUrl}/runs/${runId}/reports/bulk-approve?minAccuracy=99.0`, { method: "POST" });
    return await res.json();
  },

  async deltaSync(runId) {
    const res = await this.request(`${this.baseUrl}/runs/${runId}/delta-sync`, { method: "POST" });
    return await res.json();
  },

  async getCutoverGates(runId) {
    const res = await this.request(`${this.baseUrl}/runs/${runId}/cutover`);
    return await res.json();
  },

  async confirmCutover(runId) {
    const res = await this.request(`${this.baseUrl}/runs/${runId}/cutover`, { method: "POST" });
    return await res.json();
  },

  async getLiveLog(runId) {
    const res = await this.request(`${this.baseUrl}/runs/${runId}/log`);
    return await res.json();
  },

  // Step 4: staged reports with their resolved data sources / datasets, for local preview and edit.
  async getPreviewReports(runId) {
    const res = await this.request(`${this.baseUrl}/runs/${runId}/preview/reports`);
    if (!res.ok) throw new Error(`Failed to load staged reports (HTTP ${res.status})`);
    return await res.json();
  },

  async setDataSourceCredentials(runId, dataSourceId, userName, password, credentialRetrieval, connectString, trustServerCertificate = null) {
    const res = await this.request(`${this.baseUrl}/runs/${runId}/datasources/${dataSourceId}/credentials`, {
      method: "PUT",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify({ userName, password, credentialRetrieval, connectString, trustServerCertificate })
    });
    if (!res.ok) throw new Error(`Failed to save credentials (HTTP ${res.status})`);
    return await res.json();
  },

  async validateAndSaveDataSourceCredentials(runId, dataSourceId, userName, password, credentialRetrieval, connectString, trustServerCertificate = null) {
    const res = await this.request(`${this.baseUrl}/runs/${runId}/datasources/${dataSourceId}/validate-and-save`, {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify({ userName, password, credentialRetrieval, connectString, trustServerCertificate })
    });
    const data = await res.json().catch(() => null);
    if (!res.ok) {
      return {
        success: false,
        certUntrusted: !!(data && data.certUntrusted),
        message: data?.message || `Validation failed (HTTP ${res.status})`,
        detail: data?.detail || ""
      };
    }
    return data;
  },

  async getDesignerConfig(runId) {
    const res = await this.request(`${this.baseUrl}/runs/${runId}/designer-config`);
    return await res.json();
  },

  async pollState(runId) {
    const res = await this.request(`${this.baseUrl}/runs/${runId}/poll`);
    return await res.json();
  },

  async runLocalFolderMigration(request) {
    const res = await this.request(`${this.baseUrl}/local-folder`, {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify(request)
    });
    return await res.json();
  },

  async getLocalFolderLogs() {
    try {
      const res = await this.request(`${this.baseUrl}/local-folder/logs`);
      if (res.ok) return await res.json();
      return [];
    } catch {
      return [];
    }
  },

  async getSections(runId) {
    try {
      const res = await this.request(`${this.baseUrl}/runs/${runId}/destination/sections`);
      if (res.ok) return await res.json();
      return [];
    } catch {
      return [];
    }
  },

  async deleteDestinationReports(runId, options = {}) {
    const res = await this.request(`${this.baseUrl}/runs/${runId}/destination/delete-reports`, {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify({
        reportIds: options.reportIds || null,
        sections: options.sections || null,
        deleteAll: !!options.deleteAll,
        deleteCategories: !!options.deleteCategories
      })
    });
    return await res.json();
  },

  async bulkRemapDataSources(runId, request) {
    const res = await this.request(`${this.baseUrl}/runs/${runId}/datasources/bulk-remap`, {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify(request)
    });
    return await res.json();
  },

  async dryRunUpload(runId, conflictPolicy) {
    const q = conflictPolicy ? `?conflictPolicy=${encodeURIComponent(conflictPolicy)}` : "";
    const res = await this.request(`${this.baseUrl}/runs/${runId}/upload/dry-run${q}`, { method: "POST" });
    return await res.json();
  },

  async verifyDependencies(runId) {
    const res = await this.request(`${this.baseUrl}/runs/${runId}/dependencies/verify`);
    return await res.json();
  },

  async rebindReference(runId, reportId, request) {
    const res = await this.request(`${this.baseUrl}/runs/${runId}/reports/${reportId}/rebind-reference`, {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify(request)
    });
    return await res.json();
  }
};
