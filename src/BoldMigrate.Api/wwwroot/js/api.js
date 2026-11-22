// Bold Migrate API client

const Api = {
  baseUrl: "/api/migration",

  async getActiveRun() {
    try {
      const res = await fetch(`${this.baseUrl}/active-run`);
      if (res.ok) return await res.json();
      return null;
    } catch {
      return null;
    }
  },

  async createOrGetRun(name, sourceUrl) {
    const res = await fetch(`${this.baseUrl}/runs`, {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify({ name, sourceUrl })
    });
    return await res.json();
  },

  async testConnection(runId, sourceUrl, username, password, authMode = "Integrated") {
    const res = await fetch(`${this.baseUrl}/runs/${runId}/connect`, {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify({ sourceUrl, username, password, authMode })
    });
    return await res.json();
  },

  async testDestinationConnection(runId, destinationUrl, email, password, bearerToken) {
    const res = await fetch(`${this.baseUrl}/runs/${runId}/connect-destination`, {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify({ destinationUrl, email, password, bearerToken })
    });
    return await res.json();
  },

  async fetchEstate(runId) {
    const res = await fetch(`${this.baseUrl}/runs/${runId}/fetch`, { method: "POST" });
    return await res.json();
  },

  async checkEstate(runId) {
    const res = await fetch(`${this.baseUrl}/runs/${runId}/check`, { method: "POST" });
    return await res.json();
  },

  async startUpload(runId, action) {
    const url = action ? `${this.baseUrl}/runs/${runId}/upload?action=${action}` : `${this.baseUrl}/runs/${runId}/upload`;
    const res = await fetch(url, { method: "POST" });
    return await res.json();
  },

  async startValidation(runId) {
    const res = await fetch(`${this.baseUrl}/runs/${runId}/validate`, { method: "POST" });
    return await res.json();
  },

  async retryFailed(runId) {
    const res = await fetch(`${this.baseUrl}/runs/${runId}/retry`, { method: "POST" });
    return await res.json();
  },

  async abandonRun(runId) {
    const res = await fetch(`${this.baseUrl}/runs/${runId}/abandon`, { method: "POST" });
    return await res.json();
  },

  async getEstate(runId) {
    const res = await fetch(`${this.baseUrl}/runs/${runId}/estate`);
    return await res.json();
  },

  async getDataSources(runId) {
    try {
      const res = await fetch(`${this.baseUrl}/runs/${runId}/datasources`);
      if (res.ok) return await res.json();
      return [];
    } catch {
      return [];
    }
  },

  async getReports(runId) {
    const res = await fetch(`${this.baseUrl}/runs/${runId}/reports`);
    return await res.json();
  },

  async selectReports(runId, selectedReportIds) {
    const res = await fetch(`${this.baseUrl}/runs/${runId}/select-reports`, {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify({ selectedReportIds })
    });
    return await res.json();
  },

  async getReportRdl(runId, reportId) {
    const res = await fetch(`${this.baseUrl}/runs/${runId}/reports/${reportId}/rdl`);
    return await res.json();
  },

  async getReviewQueue(runId) {
    const res = await fetch(`${this.baseUrl}/runs/${runId}/review-queue`);
    return await res.json();
  },

  async approveReport(runId, reportId, comment) {
    const res = await fetch(`${this.baseUrl}/runs/${runId}/reports/${reportId}/approve`, {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify({ comment })
    });
    return await res.json();
  },

  async flagReport(runId, reportId, comment) {
    const res = await fetch(`${this.baseUrl}/runs/${runId}/reports/${reportId}/flag`, {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify({ comment })
    });
    return await res.json();
  },

  async bulkApprove(runId) {
    const res = await fetch(`${this.baseUrl}/runs/${runId}/reports/bulk-approve?minAccuracy=99.0`, { method: "POST" });
    return await res.json();
  },

  async deltaSync(runId) {
    const res = await fetch(`${this.baseUrl}/runs/${runId}/delta-sync`, { method: "POST" });
    return await res.json();
  },

  async getCutoverGates(runId) {
    const res = await fetch(`${this.baseUrl}/runs/${runId}/cutover`);
    return await res.json();
  },

  async confirmCutover(runId) {
    const res = await fetch(`${this.baseUrl}/runs/${runId}/cutover`, { method: "POST" });
    return await res.json();
  },

  async getLiveLog(runId) {
    const res = await fetch(`${this.baseUrl}/runs/${runId}/log`);
    return await res.json();
  },

  async getDesignerConfig(runId) {
    const res = await fetch(`${this.baseUrl}/runs/${runId}/designer-config`);
    return await res.json();
  },

  async pollState(runId) {
    const res = await fetch(`${this.baseUrl}/runs/${runId}/poll`);
    return await res.json();
  },

  async runLocalFolderMigration(request) {
    const res = await fetch(`${this.baseUrl}/local-folder`, {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify(request)
    });
    return await res.json();
  },

  async getLocalFolderLogs() {
    try {
      const res = await fetch(`${this.baseUrl}/local-folder/logs`);
      if (res.ok) return await res.json();
      return [];
    } catch {
      return [];
    }
  },

  async evaluateExpression(request) {
    const res = await fetch(`${this.baseUrl}/expression/evaluate`, {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify(request)
    });
    return await res.json();
  },

  async getExpressionSamples() {
    try {
      const res = await fetch(`${this.baseUrl}/expression/samples`);
      if (res.ok) return await res.json();
      return [];
    } catch {
      return [];
    }
  },

  async getSections(runId) {
    try {
      const res = await fetch(`${this.baseUrl}/runs/${runId}/destination/sections`);
      if (res.ok) return await res.json();
      return [];
    } catch {
      return [];
    }
  },

  async deleteDestinationReports(runId, options = {}) {
    const res = await fetch(`${this.baseUrl}/runs/${runId}/destination/delete-reports`, {
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
  }
};
