// Bold Migrate externalized strings dictionary (i18n)
// Compliant with RFC 2119 copy rules: sentence case, no exclamation marks, no emoji, typographic punctuation.

const I18N = {
  app: {
    title: "Bold Migrate",
    badge: "SSRS Migration",
    saveAndExit: "Save and exit"
  },
  steps: {
    1: "Connect",
    2: "Fetch",
    3: "Check",
    4: "Upload",
    5: "Verify",
    6: "Cut over"
  },
  hints: {
    1: {
      blocked: "Test all three connections to carry on.",
      ready: "Connections verified"
    },
    2: {
      blocked: "Fetch the estate from SSRS to carry on.",
      ready: "{count} reports found"
    },
    3: {
      blocked: "Run the check to see what will move.",
      ready: "{movable} of {total} can move"
    },
    4: {
      blocked: "Finish the upload to carry on.",
      ready: "{count} uploaded"
    },
    5: {
      blocked: "Clear the review queue to carry on.",
      ready: "Everything signed off"
    },
    6: {
      blocked: "Clear the remaining gates before switching SSRS off.",
      ready: "Ready to switch off"
    }
  },
  buttons: {
    back: "Back",
    continue: "Continue",
    testConnection: "Test connection",
    fetchEstate: "Fetch from SSRS",
    runCheck: "Run the check",
    startUpload: "Start upload",
    pause: "Pause",
    resume: "Resume",
    runAgain: "Run again",
    retryFailed: "Retry failed only",
    approveAndNext: "Approve and next",
    sendToDesigner: "Send to designer",
    bulkApprove: "Approve the rest above 99% data accuracy",
    switchOffSsrs: "Switch off SSRS",
    downloadReport: "Download the migration report",
    backToStart: "Back to the start"
  },
  scanner: {
    heading: "SSRS compatibility scanner",
    lead: "Drop your .rdl files to see what will move. Files are parsed entirely in your browser and are never uploaded.",
    dropTitle: "Drop .rdl files here",
    chooseFiles: "Choose files",
    trySample: "Try a sample report",
    privacyNotice: "Files are parsed entirely in your browser and are not uploaded to any server.",
    migrateAction: "Migrate these reports"
  }
};
