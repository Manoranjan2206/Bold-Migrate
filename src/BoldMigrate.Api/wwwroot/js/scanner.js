// Public Compatibility Scanner
// 100% Client-Side In-Browser RDL Parser. No files are uploaded to any server.

document.addEventListener("DOMContentLoaded", () => {
  const dropZone = document.getElementById("drop-zone");
  const fileInput = document.getElementById("file-input");

  dropZone.addEventListener("dragover", (e) => {
    e.preventDefault();
    dropZone.classList.add("dragover");
  });

  dropZone.addEventListener("dragleave", () => {
    dropZone.classList.remove("dragover");
  });

  dropZone.addEventListener("drop", (e) => {
    e.preventDefault();
    dropZone.classList.remove("dragover");
    if (e.dataTransfer.files && e.dataTransfer.files.length > 0) {
      processFiles(e.dataTransfer.files);
    }
  });

  fileInput.addEventListener("change", (e) => {
    if (e.target.files && e.target.files.length > 0) {
      processFiles(e.target.files);
    }
  });
});

async function processFiles(fileList) {
  const results = [];
  for (let i = 0; i < fileList.length; i++) {
    const file = fileList[i];
    const text = await file.text();
    const parsed = parseRdlInBrowser(file.name, text);
    results.push(parsed);
  }
  displayResults(results);
}

function parseRdlInBrowser(fileName, xmlText) {
  const parser = new DOMParser();
  const xmlDoc = parser.parseFromString(xmlText, "text/xml");

  // Check XML parse error
  const parserError = xmlDoc.getElementsByTagName("parsererror");
  if (parserError.length > 0) {
    return {
      name: fileName,
      schema: "Unknown",
      bucket: "Blocked",
      findings: "Not valid XML. Could not parse report definition.",
      severity: 4
    };
  }

  // Schema version detection
  const root = xmlDoc.documentElement;
  const ns = root.namespaceURI || "";
  let schema = "2016";
  if (ns.includes("2008/01")) schema = "2008";
  else if (ns.includes("2010/01")) schema = "2010";

  let bucket = "Clean";
  let findings = "All elements compatible with Bold Reports Server.";
  let severity = 1; // 1: Clean, 2: AutoFixed, 3: NeedsReview, 4: Blocked

  // 1. Blocked: CodeModule (Custom Assembly)
  if (xmlDoc.getElementsByTagName("CodeModule").length > 0) {
    bucket = "Blocked";
    findings = "References custom assembly. Cannot be migrated automatically.";
    severity = 4;
    return { name: fileName, schema, bucket, findings, severity };
  }

  // 2. NeedsReview: Embedded code calling framework types
  const codeElems = xmlDoc.getElementsByTagName("Code");
  if (codeElems.length > 0) {
    const codeContent = codeElems[0].textContent || "";
    if (codeContent.includes("System.")) {
      bucket = "NeedsReview";
      findings = "Contains embedded code that calls .NET framework types.";
      severity = 3;
    } else {
      bucket = "AutoFixed";
      findings = "Contains simple embedded code, carried over as is.";
      severity = 2;
    }
  }

  // 3. NeedsReview: Subreports
  if (xmlDoc.getElementsByTagName("Subreport").length > 0) {
    if (severity < 3) {
      bucket = "NeedsReview";
      findings = "Contains subreports. Re-check report navigation after migration.";
      severity = 3;
    }
  }

  // 4. NeedsReview: Maps / Gauges
  if (xmlDoc.getElementsByTagName("Map").length > 0 || xmlDoc.getElementsByTagName("GaugePanel").length > 0) {
    if (severity < 3) {
      bucket = "NeedsReview";
      findings = "Uses map or gauge panel controls.";
      severity = 3;
    }
  }

  // 5. AutoFixed: Shared datasets or data sources
  if (xmlDoc.getElementsByTagName("DataSourceReference").length > 0 || xmlDoc.getElementsByTagName("SharedDataSet").length > 0) {
    if (severity < 2) {
      bucket = "AutoFixed";
      findings = "References shared data source or dataset; rebound automatically during upload.";
      severity = 2;
    }
  }

  return { name: fileName, schema, bucket, findings, severity };
}

function escapeScannerHtml(text) {
  return String(text ?? "")
    .replace(/&/g, "&amp;")
    .replace(/</g, "&lt;")
    .replace(/>/g, "&gt;")
    .replace(/"/g, "&quot;")
    .replace(/'/g, "&#39;");
}

function displayResults(results) {
  // Sort worst first (highest severity first)
  results.sort((a, b) => b.severity - a.severity);

  const cleanCount = results.filter(r => r.bucket === "Clean").length;
  const autoFixedCount = results.filter(r => r.bucket === "AutoFixed").length;
  const reviewCount = results.filter(r => r.bucket === "NeedsReview").length;
  const blockedCount = results.filter(r => r.bucket === "Blocked").length;

  document.getElementById("scan-clean").innerText = cleanCount;
  document.getElementById("scan-autofixed").innerText = autoFixedCount;
  document.getElementById("scan-review").innerText = reviewCount;
  document.getElementById("scan-blocked").innerText = blockedCount;

  const tbody = document.getElementById("scan-findings-body");
  tbody.innerHTML = results.map(r => {
    let badgeClass = "badge-success";
    if (r.bucket === "Blocked") badgeClass = "badge-danger";
    else if (r.bucket === "NeedsReview") badgeClass = "badge-warning";
    else if (r.bucket === "AutoFixed") badgeClass = "badge-info";

    return `
      <tr>
        <td style="font-weight: 600;">${escapeScannerHtml(r.name)}</td>
        <td><span class="status-badge badge-neutral">RDL ${escapeScannerHtml(r.schema)}</span></td>
        <td><span class="status-badge ${badgeClass}">${escapeScannerHtml(r.bucket)}</span></td>
        <td style="color: var(--br-text-muted);">${escapeScannerHtml(r.findings)}</td>
      </tr>
    `;
  }).join("");

  document.getElementById("results-section").style.display = "block";
  document.getElementById("results-section").scrollIntoView({ behavior: "smooth" });
}

function runSampleReport() {
  const sampleXml = `<?xml version="1.0" encoding="utf-8"?>
<Report xmlns="http://schemas.microsoft.com/sqlserver/reporting/2016/01/reportdefinition">
  <DataSources>
    <DataSource Name="FinanceDS">
      <DataSourceReference>/Shared/FinanceDS</DataSourceReference>
    </DataSource>
  </DataSources>
  <Body>
    <ReportItems>
      <Textbox Name="Title">
        <Value>Monthly Financial Summary</Value>
      </Textbox>
    </ReportItems>
  </Body>
</Report>`;

  const parsed = parseRdlInBrowser("Monthly_Financial_Summary.rdl", sampleXml);
  displayResults([parsed]);
}
