# Bold Migrate — technical specification and build guide

Nov 21, 2026 · @Manoranjan

## How to use this document

This is the build specification for Bold Migrate, the SSRS to Bold Reports migration module. It is written to be implemented without further clarification, section by section, by an engineer or an autonomous coding agent working independently.

### Normative language

The words below carry their RFC 2119 meaning throughout. They are used deliberately; where a requirement says MUST, an implementation that omits it does not satisfy this specification.

| Word | Meaning |
| --- | --- |
| MUST | Required. The build is incomplete without it |
| SHOULD | Strongly recommended. Deviation requires a recorded reason |
| MAY | Optional. Left to the implementer's judgement |
| MUST NOT | Prohibited, without exception |

### Status of the existing codebase

An earlier migration solution and a set of extraction scripts exist, at `SSRSToBoldReportsMigration` and `Migration Scripts` respectively. Neither was available when this document was written, so every requirement here is specified from first principles and is implementable on its own.

When that code becomes available, it MUST be mapped against this specification rather than replaced wholesale. Three questions decide each component: does it already satisfy the requirement, does it satisfy it partially and need extending, or does it conflict with a decision taken here. Record the answer per component in a mapping appendix.

### How each section is organised

Feature sections follow the same order so that an implementer can work through them without rereading. Purpose, then behaviour, then the data it reads and writes, then its interface, then the events it logs, then the errors it raises and the message the user sees for each.

### Terminology

One word is used for each concept, everywhere, including in the user interface. The terms below are the only accepted forms; the glossary at the end of this document is the authority if the two disagree.

| Term | Means | Never written as |
| --- | --- | --- |
| Migration run | One end-to-end execution against one pair of servers | Job, task, session |
| Source server | The customer's SSRS instance | Legacy server, old server |
| Destination server | The Bold Reports Server being migrated into | Target, new server |
| Estate | Everything discovered on the source server | Inventory, catalogue |
| Report | One `.rdl` definition and its record in the system | File, RDL (as a noun for the whole report) |
| Category | The Bold Reports equivalent of an SSRS folder | Folder (when referring to the destination) |
| Validation | Comparing output from both servers for one report | Testing, verification |
| Approval | A person accepting a validation result | Sign-off, confirmation |
| Cutover | Switching the source server off | Go-live, migration completion |

### Version and ownership

This document is the single source of truth for the build. Any decision taken during implementation that contradicts it MUST be written back into the relevant section rather than held in a commit message or a chat thread. An implementation that has drifted from this document without the document being updated is a defect in both.

## Scope and definitions of done

Bold Migrate moves an SSRS report estate to Bold Reports Server and proves, report by report, that the output still matches. A report is not migrated when it has been uploaded. It is migrated when its output has been compared against the source server and either passed the thresholds automatically or been approved by a named person.

### Supported source servers

| Source | API used | Supported in version 1 |
| --- | --- | --- |
| SSRS 2017, 2019, 2022 | REST API v2.0 | Yes |
| Power BI Report Server, October 2017 and later | REST API v2.0 | Yes, for paginated reports only |
| SSRS 2016 | SOAP `ReportingService2010` | Yes, through the fallback path |
| SSRS 2012, 2014 | SOAP `ReportingService2010` | Yes, through the fallback path |
| SSRS 2008, 2008 R2 | SOAP `ReportingService2010` | No. Detected and reported, not migrated |

The destination is Bold Reports Server. The module runs inside it, so no separate destination configuration is required beyond the administrator credentials used to obtain a token.

### Definition of done for one report

All eight conditions MUST hold before the user interface shows a report as migrated.

1. The report definition has been downloaded from the source server and stored in the staging area.
2. The transform engine has rebound every data source and dataset reference to its destination identifier.
3. The report has been uploaded to the destination server and returns a successful response.
4. The report opens in the Bold Reports Web Report Viewer without raising an error.
5. It sits in the category that mirrors its source folder path.
6. Its parameters render, accept the same inputs, and carry the same default values.
7. A data comparison has run and reports matching row counts and matching totals for the same parameter set.
8. A visual comparison has run and either exceeds the configured threshold or carries an approval record naming the person who accepted it.

### Definition of done for a migration run

A migration run is complete when every report in the estate is either migrated, waived with a recorded reason, or excluded by the user before the upload stage. A run MUST NOT report itself complete while any report is in an unresolved state.

### Out of scope for version 1

These are stated plainly in the product, not discovered by the customer later. The scanner and the compatibility report MUST name each one it encounters.

| Not migrated | What the product does instead |
| --- | --- |
| Reports referencing custom assemblies | Names the assembly and the calling expressions, and adds the report to a rewrite list |
| Embedded code that calls .NET framework types | Flags the report for review and quotes the offending lines |
| Mobile Reports (`.rsmobile`) | Lists them in the estate inventory and marks them as not migratable |
| Power BI reports hosted on Power BI Report Server | Lists them and marks them as outside the product's scope |
| Report Builder semantic models (`.smdl`) | Lists them and notes that the datasets need rebuilding |
| Report snapshots and execution history | Explains that historical renders do not transfer, and recommends keeping the source server read-only for the retention period |
| Data-driven subscriptions | Records them in the subscription inventory with recipient counts |
| Custom security extensions | Detects them during the connection test and reports that the estate cannot be migrated automatically |
| My Reports personal folders | Lists them in the inventory, excluded from migration unless the user opts in |

## Architecture and deployment

Bold Migrate is a module hosted inside Bold Reports Server. It is not a separate application, not a separate installer and not a hosted service. The customer installs Bold Reports Server, which they are buying anyway, and the migration module is already there.

&#91;embedded content: deployment and component layout\]

This decision removes the largest non-technical obstacle in an SSRS replacement project. There is no second deployment to approve, no second installer to sign, no separate penetration test and no new conversation about where the customer's data goes. It also constrains the design in ways the rest of this document depends on, so the consequences are set out below rather than discovered during the build.

### Components

| Component | Responsibility |
| --- | --- |
| Migration API | Serves the user interface. One endpoint group per step, plus a polling endpoint for run state |
| Orchestrator | Owns the state machine for a migration run and drives extraction, transform, upload and validation |
| Extraction layer | Talks to the source server over REST v2.0 or SOAP. See the extraction section |
| Transform engine | Rewrites report definitions and classifies every report against the compatibility rules |
| Upload layer | Writes categories, data sources, datasets and reports to the destination item store, in order |
| Validation engine | Renders on both servers, compares output, writes scores |
| Staging store | Holds everything downloaded from the source server, on local disk, for the life of the run |
| Migration store | Holds run state, per-report records, transform decisions, validation results and the audit trail |

### Process model

The orchestrator MUST run as a hosted background service inside the server process, not inside the request that starts it. A migration run lasts minutes to hours. An implementation that ties a run to an HTTP request will fail the first time a customer closes the browser tab.

The user interface therefore starts work by posting a command and then polls for state. Every long-running operation MUST be resumable after a process restart, which means the orchestrator reads its position from the migration store rather than holding it in memory.

### Consequences the implementer must plan for

**The module ships on the server's release cadence.** It cannot be hotfixed independently. The compatibility rules MUST therefore be data the module loads at runtime, not rules compiled into it, so a wrong classification can be corrected without waiting for a server release. See the transform section.

**Validation competes with production.** Rendering several hundred reports twice on a server that is also serving the customer's live reports will be noticed. The orchestrator MUST respect a concurrency cap and SHOULD support a scheduled window. See the non-functional requirements.

**Embedded-model customers have no server to host this module.** They are out of scope for the migration module and are served by the public compatibility scanner only. This is a known gap, recorded here so it is not mistaken for an oversight.

### Staging area

Everything downloaded from the source server is written under a run-scoped directory. The path MUST be configurable, MUST default to a location inside the server's own data directory, and MUST be deleted when the run is closed or abandoned. The staging area contains customer report definitions and MUST NOT be world-readable.

## Data model

Everything a migration run knows lives in the migration store. The orchestrator holds no state in memory that it cannot rebuild from these tables after a restart.

### MigrationRun

One row per migration. A server MAY hold several runs, but only one MAY be active at a time in version 1.

| Field | Type | Notes |
| --- | --- | --- |
| `Id` | GUID | Primary key |
| `Name` | string | Defaults to the source server host name and the creation date |
| `Status` | enum | `Draft`, `Connected`, `Discovered`, `Analysed`, `Uploading`, `Uploaded`, `Validating`, `Validated`, `CutOver`, `Abandoned` |
| `CurrentStep` | int | 1 to 6, drives which step the UI opens on |
| `SourceServerUrl` | string | As entered by the user |
| `SourceVersion` | string | Detected, for example `SSRS 2022` |
| `SourceApi` | enum | `RestV2`, `Soap2010` |
| `CredentialMode` | enum | `InMemory`, `DpapiLocal`, `ExternalVault` |
| `CreatedUtc`, `UpdatedUtc` | datetime |  |
| `CreatedBy` | string | Bold Reports user identifier |
| `StagingPath` | string | Absolute path to the run's staging directory |

### CatalogItem

One row per object discovered on the source server, before any judgement is made about it.

| Field | Type | Notes |
| --- | --- | --- |
| `Id` | GUID | Primary key |
| `RunId` | GUID | Foreign key |
| `ItemType` | enum | `Folder`, `DataSource`, `DataSet`, `Report`, `Resource`, `Unsupported` |
| `SourcePath` | string | Full SSRS path, for example `/Finance/Monthly/Invoice Summary` |
| `SourceItemId` | string | The source server's own identifier |
| `Name` | string | Leaf name |
| `ParentPath` | string | Used to rebuild the tree |
| `SizeBytes` | long |  |
| `ModifiedUtc` | datetime | From the source server, used by delta sync |
| `StagedFilePath` | string | Null until downloaded |
| `Hash` | string | SHA-256 of the staged file, used by delta sync |
| `DestinationItemId` | string | Null until uploaded |

### ReportRecord

Extends a `CatalogItem` of type `Report` with everything the migration decides about it. Split from `CatalogItem` so that the discovery step stays fast and judgement-free.

| Field | Type | Notes |
| --- | --- | --- |
| `CatalogItemId` | GUID | Primary key, one to one |
| `Bucket` | enum | `Clean`, `AutoFixed`, `NeedsReview`, `Blocked`, `Excluded` |
| `BlockReasonCode` | string | Null unless `Blocked`. See the error catalogue |
| `State` | enum | See the state machine below |
| `RdlSchemaVersion` | string | For example `2010`, `2016` |
| `DataSourceRefs` | json | Source references found, and what each was rebound to |
| `DataSetRefs` | json | As above |
| `ParameterCount` | int |  |
| `UploadAttempts` | int |  |
| `IdempotencyKey` | string | Stable across retries. See the upload section |
| `ExcludedReason` | string | Set when a user removes a report from the run |

### Report state machine

These are the only legal transitions. An implementation MUST reject any other move and MUST log an error when one is attempted.

| From | To | Trigger |
| --- | --- | --- |
| `Discovered` | `Staged` | The definition downloads successfully |
| `Staged` | `Analysed` | The transform engine classifies it |
| `Analysed` | `Blocked` | Classification finds an unsupported feature |
| `Analysed` | `Queued` | Classification succeeds and the user starts the upload |
| `Analysed` | `Excluded` | The user removes it from the run |
| `Queued` | `Uploading` | The upload layer picks it up |
| `Uploading` | `Uploaded` | The destination server accepts it |
| `Uploading` | `UploadFailed` | Three attempts fail |
| `UploadFailed` | `Queued` | The user retries failed items |
| `Uploaded` | `Validating` | The validation engine picks it up |
| `Validating` | `AutoPassed` | Both scores exceed their thresholds |
| `Validating` | `AwaitingReview` | Either score falls below its threshold |
| `Validating` | `ValidationFailed` | Rendering raised an error on either server |
| `AwaitingReview` | `Approved` | A person approves it |
| `AwaitingReview` | `Flagged` | A person sends it to the designer |
| `Flagged` | `Validating` | The report is re-uploaded after an edit |
| Any state | `Excluded` | The user removes it, with a reason |

### Supporting tables

| Table | One row per | Key fields |
| --- | --- | --- |
| `TransformAction` | Change the transform engine made | `ReportId`, `ActionCode`, `Before`, `After`, `AttemptNumber` |
| `UploadAttempt` | Attempt against the destination | `ReportId`, `AttemptNumber`, `HttpStatus`, `DurationMs`, `ErrorCode` |
| `ValidationResult` | Validation run for one report | `ReportId`, `DataAccuracy`, `VisualSimilarity`, `RowsSource`, `RowsDestination`, `LayoutShifts`, `ParameterSetHash`, `RunUtc` |
| `Approval` | Human decision | `ReportId`, `Decision`, `UserId`, `UserName`, `DecidedUtc`, `Comment` |
| `PermissionMapping` | Source role assignment | `SourcePath`, `Principal`, `SourceRole`, `DestinationRole`, `MappingStatus` |
| `SubscriptionRecord` | Source subscription | `ReportPath`, `SubscriptionType`, `DeliveryMethod`, `Schedule`, `RecipientCount`, `MigrationPlan` |
| `UrlMapping` | Migrated report | `SourcePath`, `DestinationItemId`, `DestinationUrl` |
| `AuditEvent` | Action a person took | `RunId`, `UserId`, `Action`, `TargetType`, `TargetId`, `OccurredUtc`, `Detail` |

### Retention

The migration store MUST survive the run so that the migration report can be produced afterwards. The staging store MUST NOT. Staged report definitions are customer intellectual property and are deleted when the run closes. Validation renders are deleted at the same time.

## Configuration

Every setting below has a working default. A customer MUST be able to complete a migration without changing any of them. Settings are read at run start, and a change takes effect on the next run rather than mid-run, so that a run's behaviour stays explainable afterwards.

### Settings

| Key | Default | Range | Purpose |
| --- | --- | --- | --- |
| `BoldMigrate.Enabled` | `true` |  | Hides the module entirely when false |
| `Staging.RootPath` | Server data directory, `/migrations` | Any writable path | Where downloaded files are held |
| `Staging.DeleteOnClose` | `true` |  | MUST NOT default to false |
| `Extraction.PreferredApi` | `Auto` | `Auto`, `RestV2`, `Soap2010` | `Auto` probes, then falls back |
| `Extraction.TimeoutSeconds` | `100` | 30 to 600 | Per request against the source server |
| `Extraction.MaxParallel` | `4` | 1 to 16 | Concurrent downloads |
| `Transform.MaxAutoFixAttempts` | `3` | 1 to 5 | Attempts before a report goes to review |
| `Transform.RulesUrl` | Bundled file | URL or path | Where the compatibility rules are loaded from |
| `Transform.RulesRefreshHours` | `24` | 1 to 720 | How often rules are refreshed |
| `Upload.MaxParallel` | `4` | 1 to 16 | Concurrent uploads |
| `Upload.MaxAttempts` | `3` | 1 to 10 | Before a report is marked failed |
| `Upload.RetryBackoffSeconds` | `2, 8, 30` |  | Fixed schedule, not exponential, so a run's duration is predictable |
| `Validation.DataAccuracyThreshold` | `99.5` | 90.0 to 100.0 | Percentage, at or above which a report passes automatically |
| `Validation.VisualSimilarityThreshold` | `95.0` | 80.0 to 100.0 | As above |
| `Validation.PixelTolerance` | `2` | 0 to 10 | Per-channel tolerance before a pixel counts as different |
| `Validation.SampleRowLimit` | `10000` | 1000 to 1000000 | Rows compared per report before sampling begins |
| `Validation.MaxParallel` | `2` | 1 to 8 | Deliberately lower than upload. Rendering is expensive |
| `Validation.RenderDpi` | `96` | 96 or 150 | MUST be identical on both servers |
| `Run.ScheduledWindow` | Not set | Cron expression | When set, heavy stages only run inside it |
| `Logging.Level` | `Information` | `Trace` to `Error` | See the logging section |
| `Logging.RetentionDays` | `90` | 7 to 365 |  |
| `UrlShim.Enabled` | `false` |  | Switched on explicitly at cutover |
| `UrlShim.ListenPath` | `/ReportServer` |  | Must match the old SSRS path |

### Where settings live

Settings MUST be exposed through the Bold Reports Server administration interface, not only through a configuration file, because the product is self-serve and the person running the migration may not have file system access to the server.

A setting changed in the administration interface MUST be written to the same store the server uses for its own settings. The module MUST NOT introduce a second configuration mechanism.

### Threshold defaults are a product decision

The two validation thresholds carry more weight than any other setting. Set them too low and the product signs off reports that are wrong, which is the one failure the customer cannot forgive. Set them too high and the review queue fills with reports that differ only by font rendering, which makes the product feel unusable.

The defaults above assume data accuracy is the trustworthy signal and visual similarity is advisory. An implementation MUST NOT allow data accuracy to be set below 99.0 without an explicit confirmation step naming the risk.

## SSRS extraction layer

The extraction layer reads the source server and writes nothing to it. This is a hard rule, not a default. No code path in this module may issue a write to the source server, and the connection test MUST state that the account only needs read access.

### One interface, two adapters

Define a single `ISourceCatalog` interface and implement it twice, once over REST v2.0 and once over SOAP. The orchestrator, the transform engine and everything above them MUST be unaware of which adapter is in use. Building the interface first is what keeps SOAP support cheap; retrofitting it later is what makes it expensive.

The interface needs these operations and no more.

| Operation | Returns |
| --- | --- |
| `ProbeAsync` | Detected version, API in use, authentication mode that succeeded |
| `ListFoldersAsync` | Every folder path on the server |
| `ListItemsAsync` | Every catalogue item with type, path, modified date and size |
| `GetDefinitionAsync` | The raw bytes of a report, dataset or data source definition |
| `GetDataSourcesForItemAsync` | Data source references for one report |
| `GetParametersAsync` | Parameter definitions, including defaults and valid values |
| `GetPoliciesAsync` | Role assignments on one item |
| `ListSubscriptionsAsync` | Subscriptions, with delivery settings |
| `RenderAsync` | One report rendered to a given format for a given parameter set |

### Detection order

The user MUST NOT be asked which API their server speaks. Version selection in the user interface defaults to `Detect automatically` and the module probes in this order, stopping at the first success.

1. `GET {baseUrl}/api/v2.0/$metadata`. A 200 response means REST API v2.0, which covers SSRS 2017 and later and Power BI Report Server from October 2017.
2. `GET {baseUrl}/api/v1.0/CatalogItems`. A 200 response means SSRS 2016. Microsoft never supported this endpoint, so the module MUST log a warning and fall through to SOAP rather than relying on it.
3. `GET {reportServerUrl}/ReportService2010.asmx?wsdl`. A 200 response means the SOAP path is available, which covers SSRS 2008 R2 through 2022.
4. Nothing responds. Raise `SRC-001` and show the connection error described in the error catalogue.

The detected result MUST be written to `MigrationRun.SourceApi` and shown to the user as a confirmation, for example `SSRS 2022 detected. Using REST API v2.0.` A customer who sees the wrong version detected has learnt something useful before anything is migrated.

### REST v2.0 calls used

These are the expected endpoints. Before building against them, request `$metadata` from a live server and verify each one, because the OData surface differs slightly between SSRS and Power BI Report Server.

| Purpose | Call |
| --- | --- |
| Verify credentials and identity | `GET /api/v2.0/Me` |
| Enumerate everything | `GET /api/v2.0/CatalogItems` |
| Folders only | `GET /api/v2.0/Folders` |
| Shared data sources | `GET /api/v2.0/DataSources` |
| Shared datasets | `GET /api/v2.0/DataSets` |
| Download a definition | `GET /api/v2.0/CatalogItems({id})/Content/$value` |
| Data sources used by a report | `GET /api/v2.0/Reports({id})/DataSources` |
| Parameters | `GET /api/v2.0/Reports({id})/ParameterDefinitions` |
| Role assignments | `GET /api/v2.0/CatalogItems({id})/Policies` |
| Subscriptions | `GET /api/v2.0/Subscriptions` |

### SOAP calls used

| Purpose | Operation |
| --- | --- |
| Enumerate | `ListChildren(Item, Recursive: true)` |
| Item type | `GetItemType(ItemPath)` |
| Download a definition | `GetItemDefinition(ItemPath)` |
| Data sources for a report | `GetItemDataSources(ItemPath)` |
| Shared data source contents | `GetDataSourceContents(DataSource)` |
| Parameters | `GetItemParameters(ItemPath)` |
| Role assignments | `GetPolicies(Item)` |
| Subscriptions | `ListSubscriptions(Report)` |

### Authentication

| Mode | Implementation |
| --- | --- |
| Windows integrated | `HttpClientHandler` with `Credentials` set to a `NetworkCredential`, `PreAuthenticate` true |
| Windows, current service account | `UseDefaultCredentials` true, no credential entry in the user interface |
| Basic | `Authorization: Basic`, permitted over HTTPS only. The module MUST refuse Basic over plain HTTP and say why |
| Forms | Authenticate, then carry the cookie. Custom security extensions are out of scope |

When the module and the source server are on different machines and Windows integrated authentication is used, Kerberos delegation must be configured or the second hop will fail. The failure returns a 401 that looks identical to a wrong password, so the connection test MUST distinguish them. See `SRC-004` in the error catalogue.

### Download rules

- Definitions are written to the staging directory under the run, in a tree mirroring `SourcePath`.
- A byte order mark at the start of a definition MUST be stripped before parsing. SSRS emits one and several XML parsers reject it.
- The declared encoding in the XML prolog MUST be honoured rather than assumed to be UTF-8.
- A SHA-256 hash of each staged file MUST be recorded. Delta sync depends on it.
- Downloads run at `Extraction.MaxParallel`. A 429 or 503 from the source server MUST halve the parallelism for the remainder of the run and log a warning.
- A download that fails three times leaves the item in `Discovered` and does not fail the run. The estate inventory reports it as not retrieved.

## Transform and compatibility engine

The transform engine reads each staged report definition as XML, rewrites what it can, classifies what it cannot, and records every change it makes. It is the part of the product customers judge, because it decides how much work is left for a person.

### Parsing

Report definitions are parsed with `System.Xml.Linq`. Regular expressions MUST NOT be used to rewrite report definitions; they break on nested elements and on definitions that use a non-default namespace prefix.

Three report definition schemas are in circulation. The engine MUST detect which one it is holding from the root namespace and MUST preserve it on output unless an upgrade is required.

| Schema | Namespace | Produced by |
| --- | --- | --- |
| RDL 2008 | `.../reporting/2008/01/reportdefinition` | SSRS 2008 |
| RDL 2010 | `.../reporting/2010/01/reportdefinition` | SSRS 2008 R2 through 2014 |
| RDL 2016 | `.../reporting/2016/01/reportdefinition` | SSRS 2016 and later |

### Rewrite rules

Each rule below runs in order. Every rule that fires MUST write a `TransformAction` row recording what changed, so that a user can see the before and after for any report.

| Code | What it finds | What it does |
| --- | --- | --- |
| `TR-REBIND-DS` | `DataSourceReference` pointing at a shared data source path | Replaces the reference with the destination data source identifier |
| `TR-EMBED-DS` | An embedded `ConnectionProperties` block | Rewrites the connection string to the migrated equivalent, leaving credentials to the destination server |
| `TR-MAP-DSET` | `SharedDataSet/DataSetReference` where a one-to-one destination dataset exists | Repoints the reference |
| `TR-INLINE-DSET` | `SharedDataSet/DataSetReference` where no one-to-one mapping is available for that connector | Inlines the dataset query, fields and parameters into the report and removes the reference |
| `TR-PATH-SUB` | `Subreport/ReportName` holding a source path | Rewrites to the destination path |
| `TR-PATH-DRILL` | `Drillthrough/ReportName` | Rewrites to the destination path |
| `TR-PATH-IMG` | `Image` with `Source` of `External` and a server-relative path | Rewrites to the destination resource path |
| `TR-SCHEMA-UP` | Root namespace is RDL 2008 or 2010 and the destination requires 2016 | Upgrades the namespace and any renamed elements, then revalidates against the schema |
| `TR-STRIP-BOM` | Byte order mark at the start of the file | Removes it |

### Classification rules

Classification assigns exactly one bucket per report. When several rules fire, the most severe wins, in the order blocked, needs review, auto-fixed, clean.

| Code | Detected by | Bucket | Message shown to the user |
| --- | --- | --- | --- |
| `CF-ASSEMBLY` | A `CodeModules/CodeModule` element | Blocked | References a custom assembly, which cannot be migrated |
| `CF-CODE-NET` | A `Code` element referencing a .NET type | Needs review | Contains embedded code that calls a framework type |
| `CF-CODE-SIMPLE` | A `Code` element using only arithmetic and string operations | Auto-fixed | Contains simple embedded code, carried over as is |
| `CF-SUBREPORT` | A `Subreport` element | Needs review | Contains a subreport, so check navigation after upload |
| `CF-LOOKUP` | `Lookup`, `LookupSet`, `RunningValue`, `Previous` or `Aggregate` in an expression | Needs review | Uses a scoped aggregate function |
| `CF-MAP` | A `Map` element | Needs review | Uses a map |
| `CF-GAUGE` | A `GaugePanel` element | Needs review | Uses a gauge |
| `CF-INTERACTIVE-SORT` | A `UserSort` element | Needs review | Uses interactive sorting |
| `CF-RICHTEXT` | `MarkupType` set to `HTML` | Needs review | Uses rich text inside a text box |
| `CF-SHARED-DSET` | `SharedDataSet` reference | Auto-fixed | Uses a shared dataset |
| `CF-SHARED-DS` | `DataSourceReference` | Auto-fixed | Uses a shared data source |
| `CF-DOCMAP` | `DocumentMapLabel` present | Needs review | Uses a document map |
| `CF-UNSUPPORTED-TYPE` | Item is `.rsmobile`, `.smdl` or a Power BI report | Blocked | This item type is not migrated |

### The rules file

Classification rules MUST be loaded from data, not compiled in, because the module ships on the server's release cadence and a wrong classification cannot wait for a release to be corrected.

The file is JSON. Each rule carries an identifier, an XPath expression evaluated against the report definition, the bucket it assigns, the message shown to the user, and the address of the knowledge base article that explains what to do. A rule with no article address MUST fail validation at load time, because a blocked report without a documented workaround is a support ticket by design.

```json
{
  "version": "2026.11.1",
  "rules": [
    {
      "code": "CF-ASSEMBLY",
      "xpath": "//*[local-name()='CodeModule']",
      "bucket": "Blocked",
      "message": "References a custom assembly, which cannot be migrated.",
      "article": "https://support.boldreports.com/kb/custom-assemblies"
    }
  ]
}
```

### The auto-fix loop

This is the behaviour that decides how much work the customer is left with, and it is worth building properly.

1. Apply every rewrite rule that matches.
2. Validate the result against the destination report definition schema. If it fails, record the schema error.
3. Submit the result to the destination server's validate-only endpoint, if one is available, or perform a trial upload into a temporary category.
4. If either check fails, feed the error back into the rewrite stage and try again, up to `Transform.MaxAutoFixAttempts`.
5. On the final failure, move the report to `NeedsReview` and record every attempt and every error, so the person who picks it up can see what was already tried.

The user interface MUST show this loop running rather than hiding it. A progress message naming the attempt — `Attempt 2 of 3 on 11 reports` — is what makes the product feel like it is working on the customer's behalf rather than simply failing slowly.

## Upload layer

The upload layer writes the transformed estate into the destination item store. Because the module runs inside Bold Reports Server, it SHOULD call the server's internal item services directly rather than looping back through the public HTTP API. Going over HTTP to the process you are already running in adds latency, a second authentication hop and a second failure mode for no benefit.

Where the internal services are not accessible, the public API is the fallback and the calls are listed below. Verify each against the running server's API documentation before building, because the item API has changed shape between major versions.

| Purpose | Call |
| --- | --- |
| Obtain a bearer token | `POST /api/v1.0/token` |
| Create a category | `POST /api/v1.0/items`, item type `Category` |
| Create a data source | `POST /api/v1.0/items`, item type `Datasource` |
| Create a dataset | `POST /api/v1.0/items`, item type `Dataset` |
| Create a report | `POST /api/v1.0/items`, item type `Report`, with the definition as the payload |

### Dependency order

The order is not a preference. A report cannot be written before the dataset it reads, and a dataset cannot be written before its data source. The four stages run strictly in sequence and MUST NOT overlap.

1. **Categories**, mirroring the source folder tree, parents before children.
2. **Data sources**, with the connection strings and credentials collected at step one.
3. **Datasets**, each bound to the data source identifier returned in stage two.
4. **Reports**, each bound to the data source and dataset identifiers returned above.

Within a stage, items upload in parallel up to `Upload.MaxParallel`. Between stages, the layer MUST wait for every item in the previous stage to reach a terminal state.

### Idempotency

Every item carries a stable idempotency key, computed once and stored on the record.

```
IdempotencyKey = SHA256(RunId + "|" + ItemType + "|" + SourcePath)
```

The key MUST NOT include the content hash, the attempt number or a timestamp. Including any of those would generate a new key on retry, which is exactly the duplicate the key exists to prevent.

Before creating an item, the layer checks whether that key is already recorded with a destination identifier. If it is, the item is treated as already uploaded and the stage moves on. This makes a resumed run safe, makes a retry safe, and makes a second run against the same destination an update rather than a second copy of the estate.

If the destination server supports an upsert or an external identifier on items, use it and record that the key was honoured server-side. If it does not, the key is enforced in the migration store, which is weaker because it cannot detect an item deleted by someone else, and this limitation MUST be stated in the run log.

### Retry and failure

| Condition | Behaviour |
| --- | --- |
| HTTP 408, 429, 502, 503, 504, or a socket timeout | Retry on the fixed backoff schedule, up to `Upload.MaxAttempts` |
| HTTP 401 | Refresh the token once, then retry. A second 401 fails the run, not the item |
| HTTP 400 with a validation message | Do not retry. Mark the report `UploadFailed` and record the server's message verbatim |
| HTTP 409 | Treat as already present. Resolve the existing identifier and continue |
| Any other 5xx | Retry on the backoff schedule |

A failed item MUST NOT fail the run. A run reports its result as a count, for example `118 of 120 uploaded`, and the two failures appear on the retry list with their reasons.

### Partial failure and rollback

There is no rollback. A half-finished upload leaves real items on the destination server, and deleting them automatically risks deleting something the customer created themselves between stages.

Instead the layer MUST make partial state recoverable. Every created item is recorded with its key and identifier as soon as the destination server confirms it, before the next item starts. A run interrupted by a process restart resumes from the migration store and skips everything already confirmed.

The user interface MUST offer `Retry failed only`, which re-queues items in `UploadFailed` without touching anything already uploaded.

### Pause and resume

Pause MUST take effect between items, never mid-item. The control is a flag the orchestrator checks before dequeuing the next item. An implementation that aborts an in-flight request to pause will leave items in an unknown state and MUST NOT be accepted.

## Validation engine

Validation is what the product is for. Everything before it moves files; this is the part that lets a customer tell their change board the reports are the same.

Two scores are produced for every report, and they are not equal in weight. Data accuracy is evidence. Visual similarity is advice. The user interface, the migration report and the thresholds all treat them that way.

### Choosing the parameter set

Both servers MUST be rendered with provably identical inputs, or the comparison means nothing.

1. Read the parameter definitions from the source server.
2. For each parameter, use its default value. Where there is no default, take the first entry from its valid values list. Where there is neither, use a type-appropriate neutral value and mark the report as rendered with a substituted parameter.
3. Serialise the resulting set in a canonical order and store `ParameterSetHash` on the result row.
4. Send the same set to both servers.

A report rendered with a substituted parameter MUST be shown differently in the review queue, because the comparison is weaker. The user SHOULD be able to supply a parameter set of their own and re-run the comparison.

### Visual comparison

1. Export the report to PDF from the source server, and to PDF from the destination server, both at `Validation.RenderDpi` and the same page size.
2. Rasterise every page of both documents to images at the same resolution.
3. If the page counts differ, record the difference, set visual similarity to zero and raise `VAL-PAGECOUNT`. A pixel comparison across a page offset produces a meaningless number and MUST NOT be attempted.
4. Compare the images pixel by pixel. A pixel counts as different when any channel differs by more than `Validation.PixelTolerance`.
5. Group adjacent differing pixels into regions. A region above a minimum area counts as one layout shift, and the count is reported alongside the score.
6. Produce a highlighted overlay image showing the differing regions. Store it with the result; the pixel diff view in the user interface displays it.

```latex
\text{VisualSimilarity} = \left(1 - \frac{\text{differing pixels}}{\text{total pixels}}\right) \times 100
```

### Data comparison

1. Export the report to XML or CSV from both servers for the same parameter set.
2. Compare row counts first. A mismatch is reported as the headline finding, because it means the query returned different data and no cell comparison will explain that.
3. Compare cells for the first `Validation.SampleRowLimit` rows. Beyond that limit, sample evenly across the result and say in the result that sampling was used.
4. Compare the totals of every numeric column across the full result, not the sample. A total that matches while sampled cells match is strong evidence; a total that differs while the sample matches is the case worth catching.
5. Normalise before comparing: trim trailing whitespace, compare numbers by value rather than by formatted string, and compare dates as instants rather than as text. An implementation that compares formatted strings will report differences that are purely regional formatting.

```latex
\text{DataAccuracy} = \frac{\text{matching cells}}{\text{cells compared}} \times 100
```

### The font problem

Font substitution is the largest source of false differences, and an unhandled false difference destroys trust in every other number on the screen.

Before the first report is validated, the engine MUST run a calibration pass: render one small reference report, known to be simple, on both servers and measure the visual similarity. If that baseline falls below 99 percent, the two servers do not have the same fonts available, and the user MUST be told so before the queue fills with reports that look wrong and are not.

The message is specific, not generic: name the fonts the report definitions request that the destination server cannot resolve, and say that installing them will remove most of the differences.

### Scoring and routing

| Condition | Outcome |
| --- | --- |
| Data accuracy at or above threshold and visual similarity at or above threshold | `AutoPassed`. No human sees it |
| Data accuracy at or above threshold, visual similarity below | `AwaitingReview`, reason `Below the visual threshold` |
| Data accuracy below threshold | `AwaitingReview`, reason naming the row or total mismatch. This one is always shown first in the queue |
| Either render raised an error | `ValidationFailed`, with the server's message recorded |

The review queue MUST be ordered worst first, by data accuracy ascending and then visual similarity ascending. Alphabetical ordering is the wrong default and makes the queue feel endless.

### Storage

Rendered PDFs, rasterised pages and overlay images are large and contain customer data. They MUST be written to the staging area, MUST be deleted when the run closes, and MUST NOT be included in a support bundle.

## Access and subscriptions

Wrong permissions after a cutover is a security incident, not a defect. The module therefore never guesses and never silently drops an assignment it cannot resolve.

### Permission mapping

Role assignments are read from the source server during discovery and written to `PermissionMapping`, one row per folder and principal. Nothing is applied until the user chooses to apply it, at step six.

The proposed mapping below is the default. Destination role names MUST be read from the live server at run time rather than hard coded, and the user MUST be able to change any row before applying.

| Source role | Proposed destination role | Notes |
| --- | --- | --- |
| Browser | The lowest role that permits viewing and exporting | The common case, usually most of the rows |
| Content Manager | The role that permits creating, editing and deleting items | Usually a small number of administrators |
| Publisher | The role that permits creating and editing but not managing security | Where no equivalent exists, map to the editing role and flag the row |
| My Reports | No equivalent | Not applied. Reported in the inventory |
| Report Builder | No equivalent | Not applied. Reported in the inventory |

### Unmatched principals

A source principal with no destination equivalent MUST appear in an `Unmatched` list with its name and the reason, and MUST NOT be applied. Common reasons are a service account, a directory group with no members, and an account belonging to someone who has left.

The cutover checklist MUST NOT pass while unmatched principals exist unless the user explicitly waives them. Waiving writes an audit event naming the person who waived and the principals waived.

### Directory groups

Where the source assignment is to a directory group and the destination server is connected to the same directory, the group is mapped by name and the mapping is marked as resolved against the directory rather than against a local user list. Where the destination is not connected to a directory, every group becomes an unmatched principal and the user has to decide. The module MUST say which of these two situations it is in, because the amount of manual work differs enormously.

### Subscription inventory

Subscriptions are not migrated in version 1. They are inventoried, completely, because a subscription nobody knew about is the most common reason a customer turns SSRS back on after a cutover.

Each row records the report, the subscription type, the delivery method, the schedule, the recipient count and the plan.

| Subscription type | Recorded | Plan in version 1 |
| --- | --- | --- |
| Standard, email delivery | Recipients, subject, schedule, render format | Listed with the settings needed to recreate it by hand |
| Standard, file share delivery | Path, file name, schedule, render format, overwrite behaviour | As above |
| Standard, SharePoint delivery | Library, schedule | As above |
| Data-driven, any delivery | The driving query, the recipient count at the time of reading | Flagged as having no direct equivalent. Needs a product decision |
| Shared schedules | Name, recurrence, the subscriptions that reference it | Listed first, because subscriptions depend on them |

The recipient count for a data-driven subscription MUST be obtained by running the driving query read-only, or, where that is not possible, reported as unknown rather than estimated. A number the customer cannot trust is worse than no number.

### Linked reports

An SSRS linked report shares a definition with a parent report but carries its own parameter defaults. There is no equivalent, so each linked report MUST be materialised as its own report in the destination with its defaults written into the definition, and the migration report MUST list every one of them so that the customer knows a copy now exists where a link used to be.

## Cutover, delta sync and the URL shim

Most migrations stall after the reports have moved. Two things cause it: reports edited on the source server while the migration was running, and the hundreds of links to source reports buried in intranet pages, spreadsheets, emails and other applications that nobody has an inventory of.

### Delta sync

Both servers run side by side during the parallel run, so the staged copy goes stale. Delta sync brings it up to date and MUST be runnable repeatedly.

1. Re-enumerate the source catalogue and compare `ModifiedUtc` against the stored value for every item.
2. For anything newer, download it again and compare the SHA-256 hash. A modified date that moved without the content changing is common and MUST NOT trigger rework.
3. For anything genuinely changed, re-run the transform, re-upload using the same idempotency key, which updates rather than duplicates, and re-validate.
4. Report the result as a count of items checked, items changed and items re-validated.
5. Detect and report items that were deleted from the source server since extraction. These are not deleted from the destination automatically; they are listed for the user to decide.

Delta sync SHOULD be schedulable nightly during the parallel run, and MUST be runnable on demand from the cutover step.

### The URL compatibility shim

This is the highest-leverage feature in the product and the one most likely to be cut for time. It should not be.

A customer with 120 reports typically has several hundred links to those reports spread across systems they do not control. Switching the source server off breaks all of them at once. The shim answers requests at the old address and redirects each one to the right report on the destination server, so nothing has to be found and fixed.

The mapping table is built during upload, because the upload layer is the only component that knows both the old path and the new identifier. Every migrated report writes a `UrlMapping` row.

**Parameter translation.** The shim accepts the SSRS URL access syntax and translates it.

| Source parameter | Meaning | Translation |
| --- | --- | --- |
| `?/Folder/Report` | The report path, as the first query token | Looked up in `UrlMapping`, becomes the destination report address |
| `rs:Command=Render` | Render the report | The default viewer action |
| `rs:Format=PDF`, `EXCELOPENXML`, `WORDOPENXML`, `CSV`, `IMAGE` | Export directly in that format | The equivalent export format on the destination |
| `rs:ParameterLanguage` | Culture for parameter parsing | Passed through where supported |
| `rs:ClearSession` | Discard the cached session | No equivalent. Ignored |
| `rc:Toolbar`, `rc:Parameters` | Viewer chrome | Mapped to the equivalent viewer options |
| Any other name | A report parameter | Passed through by name |
| `/Reports/Pages/Report.aspx?ItemPath=...` | The Report Manager form of the same link | Recognised, with the path read from `ItemPath` |

**Behaviour.** A request whose path is not in the mapping table MUST return a page that says the report was not migrated and names the path requested, not a generic 404. That page is the only diagnostic the customer gets for a link nobody knew about, and the path is the thing they need.

The shim listens on `UrlShim.ListenPath`, which defaults to `/ReportServer` so that repointing one DNS record is enough. It is off by default and switched on deliberately at cutover.

Every request MUST be logged with the path requested, whether it resolved, and the destination it redirected to. After a month of logs the customer has the inventory of report links they never had.

### Readiness gates

The cutover step shows a checklist. A gate is either ready, open, or waived with a reason and a name attached.

| Gate | Passes when |
| --- | --- |
| Every report resolved | No report is in `AwaitingReview`, `UploadFailed` or `ValidationFailed` |
| Permissions applied | Every mapping is applied or waived |
| Unmatched principals resolved | The unmatched list is empty or waived |
| Subscriptions accounted for | Every subscription is recreated or waived |
| Delta sync current | A delta sync has run since the last change on the source server |
| URL shim active | The shim is enabled and the mapping covers every migrated report |
| Rollback agreed | The user has confirmed the source server stays available, read-only, for a stated period |

### The migration report

Produced as a PDF at cutover, and the artefact the customer takes to their change board. It MUST contain the run identifier and dates, both server addresses and versions, counts by final state, the full per-report table with both scores and the approver and timestamp for each approved report, every waived item with its reason and who waived it, the transform actions summarised by type, the subscription inventory, and the permission mapping as applied.

It MUST NOT contain credentials, connection strings or rendered report data.

## Design system

The module uses the Bold Reports theme. It sits inside Bold Reports Server, so a customer who notices it looks like a different product has found a defect.

**One substitution is outstanding.** The values below were derived from the approved interface designs for this module. Before release they MUST be replaced with the official Bold Reports brand tokens taken from the design system, and the module MUST consume the server's own theme variables where the server exposes them rather than redeclaring them. Every value lives in one stylesheet as custom properties, so the substitution is a single file change rather than a search through components.

### Colour tokens

| Token | Value | Used for |
| --- | --- | --- |
| `--br-primary` | `#1D4ED8` | Primary buttons, active step, progress fill, links |
| `--br-primary-hover` | `#1A44C0` | Primary button hover |
| `--br-primary-soft` | `#E8EEFD` | Selected rows, active drop zone, info backgrounds |
| `--br-navy` | `#0D1626` | Dark surfaces, the live log, toasts |
| `--br-surface` | `#FFFFFF` | Cards, tables, inputs |
| `--br-background` | `#F4F6FA` | Page background |
| `--br-border` | `#E3E8EF` | Card and table borders |
| `--br-border-strong` | `#CBD5E1` | Input borders, secondary buttons |
| `--br-text` | `#0B1220` | Body text and headings |
| `--br-text-muted` | `#4B5A6E` | Secondary text, labels, help text |
| `--br-text-subtle` | `#8FA0B4` | Disabled text, placeholder |
| `--br-success` / `--br-success-bg` | `#15803D` / `#DCFCE7` | Passed, connected, approved |
| `--br-warning` / `--br-warning-bg` | `#B45309` / `#FEF3C7` | Needs review, waived, blocked by a decision |
| `--br-danger` / `--br-danger-bg` | `#B91C1C` / `#FEE2E2` | Failed, cannot move, destructive actions |
| `--br-info` / `--br-info-bg` | `#1D4ED8` / `#DBEAFE` | In progress, uploading, running |

Status colour MUST NOT be the only carrier of meaning. Every status also carries a word, because roughly one man in twelve cannot reliably separate the success and warning hues above.

### Typography

The type family follows the server's own. Where the server does not set one, the stack is `Inter, "Segoe UI", system-ui, -apple-system, Arial, sans-serif`.

| Role | Size | Weight | Letter spacing |
| --- | --- | --- | --- |
| Step heading | 34px | 700 | -0.022em |
| Section heading | 21px | 700 | -0.022em |
| Card heading | 18px | 700 | normal |
| Body | 15px | 400 | normal |
| Secondary and help text | 13.5px | 400 | normal |
| Table header | 13px | 600 | normal |
| Metric value | 32px | 700 | -0.03em, tabular numerals |
| Status badge | 12.5px | 600 | normal |
| Monospace, for logs and paths | 12.5px | 400 | `ui-monospace, Menlo, Consolas, monospace` |

Every number that changes while the user watches MUST use tabular numerals, or counters will jitter as they climb.

### Spacing and shape

Spacing uses a 4px base: 4, 8, 12, 16, 20, 24, 32, 40. Card padding is 24px, grid gaps are 16px or 18px, and the content column is capped at 1080px so that line lengths stay readable on wide monitors.

Corner radius is 14px for cards, 9px for buttons and inputs, 8px for inner boxes, and fully rounded for status badges. Shadows are not used except on the toast; depth comes from borders.

### Components

| Component | Specification |
| --- | --- |
| Primary button | `--br-primary` fill, white text, 10px by 18px padding, 9px radius, 600 weight. Disabled at 40 percent opacity with `not-allowed` cursor |
| Secondary button | White fill, `--br-border-strong` border, body text colour. Border darkens on hover |
| Status badge | Pill, 4px by 11px padding, paired background and text tokens from the status set |
| Card | White, 1px `--br-border`, 14px radius, 24px padding |
| Metric card | Card containing a 32px tabular number and a 13.5px muted label |
| Table | No vertical rules. 1px `--br-border` between rows, header in 13px 600 muted, 13px by 12px cell padding |
| Input and select | 1px `--br-border-strong`, 9px radius, 10px by 12px padding, label above in 13.5px muted |
| Progress bar | 9px high, fully rounded, `#E9EDF4` track, fill transitions over 450ms |
| Step rail | Horizontal, one item per step, 3px bottom border in `--br-primary` on the active step, a numbered circle that becomes a tick when complete |
| Live log | `--br-navy` background, monospace, 1.85 line height, success lines in `#86EFAC`, warnings in `#FCD34D`, errors in `#FCA5A5` |
| Toast | `--br-navy`, white text, fixed above the action bar, dismissed after 3.6 seconds |

### Motion

Step transitions fade and rise 12px over 340ms. Progress bars and counters animate over 450ms. Nothing else moves. Every animation MUST be disabled under `prefers-reduced-motion: reduce`.

### Accessibility

Text MUST meet a 4.5 to 1 contrast ratio against its background, and large headings 3 to 1. Every control MUST be reachable and operable by keyboard, with a visible focus ring of 2px in `--br-primary` offset by 2px. Progress and status changes MUST be announced to assistive technology through a polite live region, because a user who cannot see the progress bar otherwise has no way to know a run has finished.

## User interface

The interface is a single guided journey, not a set of pages. A migration is one trip with six legs, and at every moment there is exactly one right thing to do next. The design that preceded this one used a sidebar and a page per area; it was rejected because it asked the user to decide where to go, which is the one question the product exists to answer for them.

### Persistent chrome

Three elements are present on every step and MUST NOT scroll away.

| Element | Contents | Behaviour |
| --- | --- | --- |
| Top bar | Product name, the source and destination addresses, `Save and exit` | Sticky. The addresses confirm which servers are in play without opening settings |
| Step rail | Six steps: Connect, Fetch, Check, Upload, Verify, Cut over | Completed steps are ticked and clickable. The current step is underlined. Later steps are disabled and MUST NOT be clickable |
| Progress line | A 3px line under the rail | Fills to one sixth per completed step |
| Action bar | `Back`, a status hint, and the forward button | Fixed to the bottom. The forward button is disabled until the step's exit gate passes |

### The status hint is the product's voice

When the forward button is disabled, the hint says what is missing, in those words. When the gate passes, the hint confirms it in green. These strings are specified, not left to the implementer.

| Step | Hint while blocked | Hint when ready |
| --- | --- | --- |
| 1 | Test all three connections to carry on. | Connections verified |
| 2 | Fetch the estate from SSRS to carry on. | 120 reports found |
| 3 | Run the check to see what will move. | 118 of 120 can move |
| 4 | Finish the upload to carry on. | 118 uploaded |
| 5 | Clear the review queue to carry on. | Everything signed off |
| 6 | Clear the remaining gates before switching SSRS off. | Ready to switch off |

Counts in these strings are live values, not literals.

### Step 1, Connect

Heading `Connect both servers`. Lead: `Nothing is read from SSRS until all three tests pass. SSRS never returns stored data source passwords, so those are the one thing you have to type.`

Three panels: source server, destination server, database credentials. Each has its own test control and its own result badge, so a failure points at one thing rather than at the whole step.

| Field | Control | Default |
| --- | --- | --- |
| Report server URL | Text | Empty, placeholder `https://ssrs.example.com/reports` |
| Version | Select | `Detect automatically` |
| Authentication | Select | `Windows (NTLM/Kerberos)` |
| Username, Password | Text, password | Empty |
| Destination server URL | Text, read-only | The server the module is running in |
| Administrator email, Password | Text, password | Empty |
| Credential storage | Select | `In memory for this run only` |
| Per data source: user and password | Text, password | User pre-filled from the connection string where present |

Badge states for each test: `Not tested`, `Connecting…`, then either a success message naming what was found — `Connected · REST v2.0`, `Token received`, `Reachable` — or the error from the catalogue. Changing any field in a panel resets that panel's badge to `Not tested`.

The storage selector MUST update its explanatory line immediately, on the same screen as the password fields.

### Step 2, Fetch

Heading `Fetch the estate`. Lead: `We walk the SSRS catalogue and pull everything down to local staging first. Nothing goes to Bold Reports in this step, so you can look at the inventory before anything moves.`

Four counters across the top: Folders, Data sources, Shared datasets, Reports. All read zero before the fetch and climb while it runs.

Left panel streams item paths as they are staged, newest first, capped at forty visible rows. Right panel lists what is being taken, each row ticking as that type completes. The primary action is a single `Fetch from SSRS` button inside the right panel, replaced after completion by `Staged locally. Nothing has been sent to Bold Reports yet.`

Empty state before fetching: `Nothing fetched yet.`

### Step 3, Check

Heading `Check what will move`. Lead: `Every report is read, rewritten where it can be, and re-tested. You only see the ones we could not settle ourselves.`

Before the run, one centred card with the count staged and a `Run the check` button. During the run, a progress bar with a message naming the current activity, including the attempt number of the auto-fix loop.

After the run, four counts — `Move clean`, `We fixed automatically`, `Need your eye later`, `Cannot move` — then a card listing what was changed on the customer's behalf, then a table of everything unresolved with its reason and a link to the knowledge base article for that reason.

### Step 4, Upload

Heading `Upload to Bold Reports`. Lead: `Strict dependency order. A report cannot land before the dataset it reads, and a dataset cannot land before its data source. Pause at any time; the run picks up where it stopped.`

Overall progress with a percentage, and a button that reads `Start upload`, then `Pause`, then `Resume`, then `Run again`. A stage table showing the four stages with running counts and status badges. A live log beside it, with a `Retry failed only` control in the header.

### Step 5, Verify

Heading `Verify the exceptions`. Lead names both numbers: how many passed automatically and how many need review.

This step is a queue that empties, not a library to browse. A counter reads `14 left to review` above a bar that fills as the queue drains. One report is shown at a time, worst first.

The review card carries the report name, its folder, its data source and the reason it is in the queue; the four metrics; a three-way view switch for `Side by side`, `Pixel diff` and `Data diff`; and three actions — `Open in designer`, `Send to designer`, `Approve and next`. Approving advances automatically to the next report.

A bulk control reads `Approve the rest above 99% data accuracy`. It MUST stop at the first report below that figure rather than skipping it, and MUST say so.

When the queue empties the step shows a completion card rather than an empty table.

### Step 6, Cut over

Heading `Switch SSRS off`. Lead: `The last two things people forget: reports edited in SSRS while you were migrating, and the hundreds of links to SSRS reports buried in intranet pages, spreadsheets and other applications.`

Left, the readiness checklist, each gate ready, open or waived. Right, two action cards: delta sync, and the URL shim with its redirect count. The forward button reads `Switch off SSRS` on this step, not `Continue`.

### Completion

A single card: a tick, the heading `SSRS is off`, the final counts, and two actions — `Download the migration report` and `Back to the start`.

### The public scanner

Outside the server and outside the journey, with no sign-in. A drop zone reading `Drop .rdl files here`, with `Choose files` and `Try a sample estate`. Files are parsed in the browser and MUST NOT be uploaded; the page says so twice, once above the drop zone and once beneath the results.

Results are four counts and a table of file, verdict and findings, sorted worst first. The closing action is `Migrate these reports`.

### Responsive behaviour

Below 900px the step rail scrolls horizontally and drops its sub-labels, grids collapse to one column, metric cards stay two across, and the server addresses are hidden from the top bar. The action bar stays fixed and MUST respect the device's safe area inset so the forward button is never under a home indicator.

## Copy and messaging rules

This product is self-serve. Nobody is on the call to interpret a message, so the words in the interface are part of the engineering, not decoration applied afterwards. Every string an implementer writes MUST follow these rules, and strings that appear in this document are final unless the product owner changes them here.

### Voice

Write as a competent colleague explaining what is happening. Not as a system reporting its own internals, and not as a marketing page.

| Write | Do not write |
| --- | --- |
| Connected. SSRS 2022, using REST API v2.0. | Connection established successfully! |
| 118 of 120 reports uploaded. Two were skipped. | Operation completed with warnings. |
| We could not reach the server at that address. | ERROR: Connection failed (code 500) |
| This report uses a custom assembly, which cannot be migrated. | Unsupported feature detected. |

### Tense

| Situation | Tense | Example |
| --- | --- | --- |
| Describing what a control will do | Present simple | `Fetch from SSRS` |
| Work in progress | Present continuous, with an ellipsis | `Connecting…`, `Rebinding data sources…` |
| Work finished | Present perfect or simple past | `118 reports uploaded`, `Two reports were skipped` |
| Current state | Present simple | `14 left to review` |
| A consequence the user should expect | Future with *will* | `Switching this on will redirect 348 existing links` |

Do not mix tenses within one message. `Fetching the estate and uploaded 12 items` is the kind of string that gets written when progress text is assembled from fragments, and it MUST be caught in review.

### Person

Use *you* for the customer and *we* for the product, sparingly. Avoid the first person for routine state: `14 left to review`, not `We found 14 reports for you to review`. Never use *I*.

### Capitalisation

Sentence case everywhere: headings, buttons, labels, table headers, status badges. Title Case Is Not Used Anywhere In This Product. Proper nouns keep their own capitalisation: SSRS, Bold Reports, Bold Reports Server, Web Report Viewer, Web Report Designer, PostgreSQL, SQL Server, Kerberos.

### Product and feature names

| Correct | Incorrect |
| --- | --- |
| Bold Reports Server | BoldReports Server, Bold Report Server |
| Web Report Viewer | report viewer, BR Viewer |
| Web Report Designer | designer, report designer |
| SSRS | ssrs, SQL Server Reporting Services on second mention |
| a report definition, or `.rdl` file | an RDL (as a noun for the whole report) |

### Numbers, dates and units

Spell out numbers below ten in prose, use figures in counts and metrics. Percentages carry one decimal place and a percent sign with no space: `99.2%`. Counts of totals are written as `118 of 120`, never `118/120` in prose, though the slash form is acceptable inside a progress label where space is tight.

Dates in the interface follow the user's locale. Dates in logs, the migration report and the audit trail are ISO 8601 in UTC, with the offset shown: `2026-11-21T14:32:08Z`. Durations are written in the largest sensible unit: `4 minutes`, not `243 seconds`.

### Buttons

A button says what happens when it is pressed, as a verb phrase: `Fetch from SSRS`, `Run the check`, `Approve and next`, `Switch off SSRS`. Never `OK`, `Submit`, `Go`, or `Yes`. The one exception is the journey's forward control, which reads `Continue` because what happens next is named by the step rail beside it.

### Punctuation

Use typographic punctuation in interface text: curly apostrophes and quotation marks, and an ellipsis character rather than three full stops. Keep straight quotes in code, paths, connection strings and anything the user will copy.

No exclamation marks. No emoji.

### Writing an error

Every error message has three parts, in this order, and all three are required.

1. **What happened**, in plain words. `We could not sign in to the SSRS server.`
2. **The most likely cause**, named specifically rather than listed. `The user name or password is wrong, or the account does not have Browse permission on the root folder.`
3. **What to do next**, as an action the user can actually take. `Check the credentials and test the connection again.`

The technical detail — status codes, exception types, request identifiers — goes into the log and the support bundle, never into the message. One exception: the correlation identifier is shown beneath the message, so that the customer can quote it in a ticket.

### Review

Every string shipped in the interface MUST be reviewed against this section before release. A spelling or tense error in a product a customer is trusting with a migration costs more credibility than it should, because it is the one thing they can check for themselves.

## Logging

There are two logs and they serve different people. Conflating them is the most common mistake in a product like this.

The **live log** is shown in the interface during the upload. It is written for the customer, in the customer's language, and it is deliberately shallow: what is being done, to what, and whether it worked. The **system log** is written for an engineer, structured, complete, and never shown in the interface.

Both are derived from the same events. An implementation MUST NOT generate them separately, or they will disagree, and the moment they disagree the customer stops trusting the one they can see.

### Log levels

| Level | Used for | Shown in the live log |
| --- | --- | --- |
| `Trace` | Request and response bodies, XPath evaluation detail. Off by default | No |
| `Debug` | Per-item decisions, rule matches, timing | No |
| `Information` | Stage transitions, item outcomes, user actions | Yes |
| `Warning` | Retries, fallbacks, skipped items, threshold misses | Yes |
| `Error` | An item failed after its final attempt | Yes |
| `Critical` | The run cannot continue | Yes |

### Structured fields

Every system log entry carries these fields. They are not optional, because a field that is sometimes present cannot be filtered on.

| Field | Example | Purpose |
| --- | --- | --- |
| `timestamp` | `2026-11-21T14:32:08.431Z` | UTC, always |
| `level` | `Warning` |  |
| `eventCode` | `UPLD-RETRY` | From the catalogue below. Stable across releases |
| `runId` | GUID | Every entry belongs to a run |
| `correlationId` | GUID | One per user action or per item, so a single report's whole life can be filtered |
| `step` | `4` | Which journey step was active |
| `itemType`, `itemPath` | `Report`, `/Finance/Invoice Summary` | Omitted for run-level events |
| `attempt` | `2` | Where retries apply |
| `durationMs` | `1840` | On every completed operation |
| `outcome` | `Success`, `Failure`, `Skipped` |  |
| `message` |  | Plain text, no interpolated secrets |
| `userId` |  | On user actions only |

### Event catalogue

| Code | Level | Raised when |
| --- | --- | --- |
| `RUN-START` | Information | A migration run is created |
| `RUN-RESUME` | Information | The orchestrator resumes a run after a restart |
| `RUN-STEP` | Information | The run moves to a new step |
| `RUN-ABANDON` | Warning | A user abandons a run |
| `RUN-COMPLETE` | Information | Cutover is confirmed |
| `CONN-PROBE` | Information | API detection runs, with the result |
| `CONN-OK` | Information | A connection test passes |
| `CONN-FAIL` | Error | A connection test fails, with the error code from the catalogue |
| `CONN-FALLBACK` | Warning | REST detection fails and SOAP is used |
| `DISC-START`, `DISC-COMPLETE` | Information | Discovery boundaries, with counts |
| `DISC-ITEM` | Debug | One item enumerated |
| `DISC-DOWNLOAD` | Debug | One definition staged, with size and hash |
| `DISC-SKIP` | Warning | An item could not be retrieved after three attempts |
| `DISC-THROTTLE` | Warning | The source server returned 429 or 503 and parallelism was reduced |
| `XFRM-START`, `XFRM-COMPLETE` | Information | Analysis boundaries, with bucket counts |
| `XFRM-RULE` | Debug | A rewrite rule fired, with before and after |
| `XFRM-ATTEMPT` | Information | An auto-fix attempt began, with the attempt number |
| `XFRM-GIVEUP` | Warning | Auto-fix exhausted its attempts, with the final error |
| `XFRM-BLOCK` | Warning | A report was classified as blocked, with the rule code |
| `XFRM-RULES-LOAD` | Information | The compatibility rules file loaded, with its version |
| `XFRM-RULES-STALE` | Warning | The rules file could not be refreshed and the cached copy is in use |
| `UPLD-STAGE` | Information | An upload stage began or finished, with counts |
| `UPLD-ITEM` | Debug | One item uploaded, with the destination identifier |
| `UPLD-RETRY` | Warning | An upload attempt failed and will be retried, with the status code |
| `UPLD-FAIL` | Error | An item failed after its final attempt |
| `UPLD-DUPLICATE` | Information | An idempotency key matched and the item was skipped |
| `UPLD-PAUSE`, `UPLD-RESUME` | Information | The user paused or resumed |
| `VALD-CALIBRATE` | Information | The font calibration pass ran, with its baseline score |
| `VALD-FONT-GAP` | Warning | Calibration fell below 99 percent, naming the unresolved fonts |
| `VALD-RENDER` | Debug | A render completed on one server, with format and duration |
| `VALD-RESULT` | Information | A comparison completed, with both scores |
| `VALD-PAGECOUNT` | Warning | Page counts differed and visual comparison was abandoned |
| `VALD-FAIL` | Error | Rendering raised an error on either server |
| `VALD-APPROVE`, `VALD-FLAG` | Information | A person approved or flagged a report. Also written to the audit trail |
| `PERM-READ` | Information | Role assignments were read, with counts |
| `PERM-UNMATCHED` | Warning | A principal could not be resolved |
| `PERM-APPLY` | Information | Mappings were applied |
| `CUT-DELTA` | Information | A delta sync ran, with items checked and changed |
| `CUT-GATE-WAIVE` | Warning | A gate was waived, with the reason and the user |
| `CUT-REPORT` | Information | The migration report was generated |
| `SHIM-ENABLE` | Information | The URL shim was switched on, with the mapping count |
| `SHIM-HIT` | Debug | A request resolved, with source path and destination |
| `SHIM-MISS` | Warning | A request did not resolve, with the path requested |
| `SEC-REDACT` | Debug | A value was redacted before writing |
| `SEC-CRED-CLEAR` | Information | Credentials were cleared from memory at run close |

### Redaction

Redaction happens at the point of writing, not at the point of export. A secret that reaches a log file has leaked, whatever the export does afterwards.

| Redact | Replace with |
| --- | --- |
| Any value from a password field | `[redacted]` |
| The `Password`, `Pwd`, `User ID`, `Uid` tokens inside a connection string | `[redacted]`, keeping the rest of the string readable |
| Bearer tokens and API keys | First four characters, then `[redacted]` |
| `Authorization` headers | The scheme only |
| Rendered report content | Never logged at any level |

The redaction routine MUST be covered by unit tests that assert on realistic connection strings for SQL Server, Oracle and ODBC. This is the test most likely to be skipped and the one whose absence causes the worst incident.

### Retention and storage

System logs are written through the server's existing logging pipeline rather than to a private file, so that a customer's log shipping already collects them. The live log for a run is stored with the run so that it can be reopened later.

### The support bundle

One control, available from the upload step and from the completion screen, produces a single archive containing the run record, the per-report states and outcomes, the system log filtered to that run, the transform actions, the validation scores, and the detected versions and settings.

It MUST NOT contain report definitions, rendered output, credentials or connection strings. The file name carries the run identifier and the date. This is the artefact a customer attaches to a support ticket, and the self-serve model depends on it being complete enough that nobody has to ask them for more.

## Monitoring and metrics

The module runs on the customer's own server, so monitoring serves two audiences with different access. The customer watches their own instance. We, on the product side, see only what the customer chooses to send us.

### Health checks

The module contributes to the server's existing health endpoint rather than adding one of its own.

| Check | Healthy when | Degraded when |
| --- | --- | --- |
| `boldmigrate.orchestrator` | The background service is running and its last heartbeat is under 60 seconds old | No heartbeat for 60 to 300 seconds |
| `boldmigrate.store` | The migration store accepts a read and a write | Reads succeed, writes fail |
| `boldmigrate.staging` | The staging path is writable and has free space above 2 GB | Free space between 500 MB and 2 GB |
| `boldmigrate.rules` | The compatibility rules loaded, and their age is under `Transform.RulesRefreshHours` twice over | Rules are stale but usable |
| `boldmigrate.source` | Reported only while a run is active. The last call to the source server succeeded | The last three calls failed |

A degraded check MUST NOT take the server itself unhealthy. A failing migration is not a reason to restart a reporting server that is serving live reports.

### Metrics

Emitted through the server's existing metrics pipeline, named with a `boldmigrate_` prefix.

| Metric | Type | Labels | What it tells you |
| --- | --- | --- | --- |
| `boldmigrate_runs_total` | Counter | `outcome` | How many migrations start and how many finish |
| `boldmigrate_run_duration_seconds` | Histogram | `step` | Where time actually goes. Expect validation to dominate |
| `boldmigrate_items_total` | Counter | `stage`, `item_type`, `outcome` | Throughput and failure rate per stage |
| `boldmigrate_item_duration_seconds` | Histogram | `stage`, `item_type` | Per-item cost, for capacity planning |
| `boldmigrate_source_requests_total` | Counter | `api`, `status` | Pressure on the source server, and whether it is throttling |
| `boldmigrate_transform_attempts_total` | Counter | `attempt`, `outcome` | Whether the auto-fix loop is earning its keep |
| `boldmigrate_classification_total` | Counter | `bucket`, `rule_code` | Which unsupported features actually appear in the field |
| `boldmigrate_validation_score` | Histogram | `kind` | Distribution of both scores. The shape tells you whether the thresholds are set right |
| `boldmigrate_review_queue_depth` | Gauge | `run_id` | Whether the queue is draining |
| `boldmigrate_approvals_total` | Counter | `decision` | Approved against flagged |
| `boldmigrate_shim_requests_total` | Counter | `outcome` | Resolved against missed. A rising miss count means links nobody knew about |
| `boldmigrate_staging_bytes` | Gauge |  | Disk pressure during a large run |

### Alerts the customer should set

The product MUST ship these as suggested alert definitions rather than leaving an administrator to invent them.

| Alert | Condition | Why it matters |
| --- | --- | --- |
| Run stalled | A run is active and `boldmigrate_items_total` has not increased for 15 minutes | The most common silent failure |
| Source server throttling | More than 20 responses of 429 or 503 in 5 minutes | The migration is pressuring a production server |
| Staging space low | Free space below 500 MB | A large estate will fail mid-run otherwise |
| Upload failure rate | More than 5 percent of items in a stage failed | Something systematic, not bad luck |
| Validation render failures | More than 10 render errors in a run | Usually a missing font, a missing data source credential, or a permission problem |
| Shim miss rate | Misses exceed 10 percent of shim requests over an hour after cutover | Real links are breaking right now |

### Product telemetry

With the customer's explicit consent, and never by default, the module MAY send back an anonymised summary: counts by bucket, the rule codes that fired and how often, score distributions, stage durations, and the detected source version. No paths, no report names, no connection strings, no customer identity.

This is the most valuable data the product can produce. Which report definition features actually appear in real SSRS estates, and how often, is the roadmap input the engine team currently does not have, and today those priorities are set by whichever customer escalated most recently.

The consent prompt MUST state exactly what is sent, MUST be refusable without any loss of function, and MUST be re-askable rather than permanent.

### Product health measures

These are reported internally, from telemetry and from support data, and they are how the product is judged after release.

| Measure | Target |
| --- | --- |
| Time from first connection to first validated report | Under 30 minutes |
| Reports migrated with no manual edit | 85 percent |
| Reports passing both thresholds automatically | 85 percent of uploaded reports |
| Reports reopened after approval | Under 2 percent |
| Support tickets per 100 reports migrated | Half the pre-release baseline |
| Runs abandoned before upload | Under 15 percent |

The baseline in the second to last row does not exist yet. It MUST be established from the last twelve months of migration support tickets before release, or the saving the product delivers cannot be stated.

## Error catalogue

The product is self-serve, so every error message is doing the job a support engineer would otherwise do on a call. The messages below are the shipped strings, not suggestions. Each follows the three-part rule in the copy section: what happened, the likely cause, what to do next.

Every error shown in the interface also displays the correlation identifier beneath it, in small muted text, so the customer can quote it in a ticket.

### Source server

| Code | Raised when | Message |
| --- | --- | --- |
| `SRC-001` | No endpoint responded during detection | We could not reach a report server at that address. The URL may be wrong, or the server may not be running. Check the address and test the connection again. |
| `SRC-002` | HTTP 401 and the probe shows the service reached the server | We could not sign in to the SSRS server. The user name or password is wrong, or the account does not have Browse permission on the root folder. Check the credentials and test again. |
| `SRC-003` | HTTP 403 | We signed in, but the account is not allowed to read the report catalogue. Grant the account the Browser role on the root folder, then test again. |
| `SRC-004` | 401 with a Kerberos failure and the module on a different host from SSRS | We signed in, but SSRS rejected the credentials when it passed them on. This is usually Kerberos delegation, not a wrong password. Either configure delegation for the Bold Reports service account, or use Basic authentication over HTTPS. |
| `SRC-005` | Only `/api/v1.0` responded | This server looks like SSRS 2016, which does not have the supported REST API. We will use the older SOAP interface instead, which works but is slower. |
| `SRC-006` | SSRS 2008 or 2008 R2 detected | This server is SSRS 2008, which Bold Migrate does not support. Reports can still be exported by hand and uploaded individually. |
| `SRC-007` | Basic authentication selected over plain HTTP | Basic authentication sends the password in a form that can be read in transit, so we will not use it over an unencrypted connection. Use HTTPS, or choose Windows authentication. |
| `SRC-008` | A custom security extension is detected | This server uses a custom security extension, which we cannot authenticate against. Contact support with your server configuration so we can advise. |
| `SRC-009` | Repeated 429 or 503 from the source | The SSRS server is refusing requests because it is busy. We have slowed down and will keep going. If this continues, run the migration outside business hours. |

### Destination server

| Code | Raised when | Message |
| --- | --- | --- |
| `DST-001` | Token request fails with 401 | We could not sign in to Bold Reports with those administrator credentials. Check the email address and password and test again. |
| `DST-002` | Token request succeeds, item creation returns 403 | That account can sign in but cannot create items. It needs administrator rights on the destination server. |
| `DST-003` | Item creation returns 400 with a validation message | Bold Reports rejected this report. The message from the server was: *{serverMessage}*. The report has been added to your review list. |
| `DST-004` | Disk or quota failure on the destination | The destination server ran out of space while saving. Free space and retry the failed items. |

### Credentials

| Code | Raised when | Message |
| --- | --- | --- |
| `CRED-001` | A data source test fails to connect | We could not connect to *{dataSourceName}* with those details. Check the user name and password for that database. |
| `CRED-002` | A connection string cannot be parsed | We could not read the connection string SSRS returned for *{dataSourceName}*. You can enter the server and database by hand instead. |
| `CRED-003` | Vault reference cannot be resolved | We could not read the secret at that reference. Check that the Bold Reports service account is allowed to read it. |

### Transform

| Code | Raised when | Message |
| --- | --- | --- |
| `XFRM-001` | The definition is not valid XML | This report definition could not be read. It may be corrupt, or it may not be a report definition at all. |
| `XFRM-002` | Auto-fix exhausted its attempts | We tried three times to make this report work and could not. The last problem was: *{lastError}*. It is on your review list with everything we tried. |
| `XFRM-003` | A blocked rule fired | *{ruleMessage}* — with a link to the knowledge base article for that rule |
| `XFRM-004` | The rules file cannot be loaded at all | We could not load the compatibility rules, so we cannot tell you what will move. Check that the server can reach the internet, or supply the rules file locally. |

### Validation

| Code | Raised when | Message |
| --- | --- | --- |
| `VALD-001` | Rendering fails on the source server | SSRS could not render this report, so there is nothing to compare against. The report may already be broken on SSRS. |
| `VALD-002` | Rendering fails on the destination | Bold Reports could not render this report. The server reported: *{serverMessage}*. |
| `VALD-003` | Page counts differ | The two renders have different page counts, so we have not scored the images. Open them side by side to see where they diverge. |
| `VALD-004` | Calibration below 99 percent | Some fonts your reports use are not installed on this server, so reports will look different without being wrong. The missing fonts are: *{fontList}*. Installing them will remove most of the differences. |
| `VALD-005` | A parameter had no default and no valid values | We had to choose a value for *{parameterName}* to render this report, so the comparison is weaker than usual. You can supply your own values and run it again. |

### System

| Code | Raised when | Message |
| --- | --- | --- |
| `SYS-001` | Staging path is not writable | We cannot write to the staging folder at *{path}*. Check the folder exists and that the Bold Reports service account can write to it. |
| `SYS-002` | Staging space exhausted mid-run | The server ran out of disk space partway through. Free space and resume; nothing already done will be repeated. |
| `SYS-003` | The orchestrator stopped unexpectedly | The migration stopped unexpectedly and has been resumed from where it left off. {resumedCount} items were already finished. |
| `SYS-004` | A second run is started while one is active | A migration is already running on this server. Finish or abandon it before starting another. |

### Diagnostic rules

Three behaviours separate a product that supports itself from one that generates tickets.

**Distinguish causes that look the same.** `SRC-002` and `SRC-004` are both a 401. Telling them apart requires checking whether the module and the source server are on the same host, and it saves the single most common multi-day support thread in SSRS migrations.

**Never show a raw exception.** A stack trace in the interface tells the customer nothing and tells them the product is not finished. It goes to the log.

**Every blocked report links to its article.** The rule that classified it carries the article address, and a rule without one fails validation at load time. A blocker with no documented workaround is a lost deal; a blocker with one is a scoped task.

## Security and audit

The module handles production database credentials and renders real customer data. It runs inside a server the customer already installed on their own network, which removes the deployment question but not the handling question.

### What is held, and for how long

| Secret | Held | Cleared |
| --- | --- | --- |
| Source server credentials | For the run, in the chosen storage mode | At run close, or when the run is abandoned |
| Destination administrator password | Not held. Exchanged immediately for a bearer token | Immediately |
| Destination bearer token | In memory, refreshed on expiry | At run close |
| Database passwords for each data source | In the chosen storage mode | At run close |
| Vault references | For the run | At run close |

### Storage modes

| Mode | Implementation | When to recommend it |
| --- | --- | --- |
| In memory for this run only | Process memory, overwritten on clear. Default | Single-sitting migrations, which is most of them |
| Encrypted on this machine | DPAPI under the service account, or an equivalent on Linux | Runs spanning several days, or scheduled delta syncs |
| A reference to your own vault | Only the reference is stored. The secret is fetched at the moment of use and never written anywhere | Customers with a secrets policy, usually the larger ones |

The secret MUST NOT be written to disk in the default mode, not even transiently, and MUST NOT appear in a crash dump path, a temporary file or an environment variable.

Why the customer is asked for database passwords at all is explained on the same screen as the fields. SSRS deliberately does not return stored data source passwords through its API. That is a property of SSRS, not a gap in this product, and saying so turns a suspicious moment into a reassuring one.

### Least privilege

| Account | Needs | Must not need |
| --- | --- | --- |
| Source server account | Browse on the folders being migrated, and read on item definitions and policies | Any write permission, Content Manager, or System Administrator |
| Destination account | Create items and categories, read roles | Server configuration rights |
| Database accounts | Enough to execute the report queries | Schema modification, or anything beyond read where the reports only read |

The connection test MUST state what the source account needs, so that a customer who hits `SRC-003` knows exactly what to ask their SSRS administrator for.

### The comparison view and mixed authentication

The side-by-side view renders the source report beside the destination report. Current browsers block Windows authentication inside an iframe when the hosting page authenticates with a bearer token or a cookie, which is exactly this situation.

The source pane MUST therefore be served through a reverse proxy inside the module, which also removes the cross-origin problem. An implementation built as two direct iframes will pass review on a developer machine and fail at every customer site. This is a design constraint, not an optimisation.

### Data handling

Staged report definitions and rendered output are customer intellectual property and customer data. They are written under the run's staging directory with file permissions restricted to the service account, and deleted when the run closes. Nothing leaves the customer's network except the anonymised telemetry described in the monitoring section, and only with consent.

### Audit trail

Every action a person takes is written to `AuditEvent`, separately from the system log, because the system log rotates and the audit trail must not.

| Action recorded | Fields beyond the standard set |
| --- | --- |
| Run created, abandoned, completed |  |
| Connection tested | Server address, result, not the credentials |
| Estate fetched | Counts |
| Check run | Bucket counts, rules version |
| Upload started, paused, resumed |  |
| Report approved | Report path, both scores, comment |
| Report flagged | Report path, both scores, comment |
| Reports bulk approved | The threshold used and the count |
| Permission mapping applied | Mappings applied, mappings waived |
| Gate waived | Which gate, the reason, the person |
| URL shim enabled | Mapping count |
| Cutover confirmed |  |
| Settings changed | Setting, old value, new value. Secrets recorded as changed, never with their values |

The audit trail is exportable as part of the migration report and MUST survive the deletion of the staging area. A customer's change board will ask who approved each report, and this is the only acceptable answer.

## Non-functional requirements

### Scale

Version 1 targets estates of up to 500 reports and MUST degrade gracefully above that rather than failing. The implementation MUST be load-tested against a generated estate of 2,000 reports before release, because the failures that appear at that size — memory held across a whole stage, unbounded result sets, timeouts tuned for 50 items — do not appear at 100 and will otherwise be found by a customer.

| Dimension | Version 1 target | Must not fail below |
| --- | --- | --- |
| Reports in one estate | 500 | 2,000 |
| Folders | 200 | 1,000 |
| Shared datasets | 200 | 1,000 |
| Single report definition size | 2 MB | 20 MB |
| Concurrent migration runs per server | 1 | 1, enforced, with `SYS-004` |

### Performance targets

Measured on a server meeting the Bold Reports Server minimum specification, against a source server on the same network.

| Operation | Target |
| --- | --- |
| Connection test, each panel | Under 5 seconds |
| Discovery of 500 items | Under 3 minutes |
| Transform and classification per report | Under 2 seconds at the median |
| Upload per report | Under 3 seconds at the median |
| Validation per report, both renders and both comparisons | Under 30 seconds at the median |
| First validated report, from the first connection test | Under 30 minutes |
| Interface response to any click | Under 200 milliseconds, or a progress indicator within 200 milliseconds |

Validation dominates total run time by an order of magnitude. Any optimisation effort belongs there and nowhere else.

### Concurrency and courtesy

The module shares a server with the customer's live reporting. It MUST behave as a guest.

- Upload and validation parallelism are capped separately, and validation defaults lower, because rendering is the expensive one.
- When `Run.ScheduledWindow` is set, discovery, upload and validation only run inside it. The interface says when the next window opens rather than appearing stalled.
- Repeated 429 or 503 responses from the source server halve parallelism for the rest of the run and raise `DISC-THROTTLE`. Parallelism is not restored automatically, because a server that pushed back once will push back again.
- The module MUST NOT consume more than half the available worker threads of the host process.

### Resumability

Every long-running stage MUST be resumable after a process restart without repeating completed work and without creating duplicates. This is tested by killing the process mid-stage, at each of the four upload stages and during validation, and confirming that the resumed run produces the same final state as an uninterrupted one.

### Browser support

The interface supports the current and previous major versions of Chrome, Edge, Firefox and Safari. The public scanner has the same support, and additionally MUST work with the file reading API disabled by showing a clear message rather than failing silently.

### Localisation

All interface strings MUST be externalised from the first commit, even though version 1 ships in English only. Retrofitting string extraction across a product of this size costs several times what doing it from the start costs, and the Bold Reports customer base is not English-only.

Dates and numbers in the interface follow the browser locale. Dates in logs, the audit trail and the migration report are always ISO 8601 in UTC and MUST NOT be localised.

### Backward and forward compatibility

The event codes in the logging section and the error codes in the catalogue are a public contract. Customers will build alerts on them and support will build searches on them. A code MUST NOT change meaning between releases. A code that becomes obsolete is retired, not reused.

## Additional features worth building

These are outside version 1. They are listed in the order I would build them, with the reasoning, so that the sequence is not re-argued every quarter.

### 1. Usage analysis before migration

The cheapest way to halve a migration is to migrate half as many reports. SSRS records every execution in its own database, in the `ExecutionLog3` view, and in most estates a large share of reports have not been run in a year.

Before the upload step, offer to read that view, with read-only access to the report server database, and show the estate split by last run: run this month, run this year, not run in a year, never run. Let the user exclude whole groups in one action.

This changes the conversation from *we have 400 reports to move* to *we have 160 reports anyone actually uses*. It shortens every project, improves every metric in the monitoring section, and costs very little to build. It is first on this list for that reason.

### 2. Subscription migration

Inventorying subscriptions, as version 1 does, tells the customer what will break. It does not stop it breaking. Until standard subscriptions are recreated on a schedule in Bold Reports, the source server stays switched on, and a migration that does not end with SSRS switched off has not finished.

This depends on the destination server exposing an API to create schedules and delivery settings. That dependency is the reason it is second rather than first.

### 3. Reviewer assignment

On an estate of any size, the person running the migration is not the person who knows whether the finance report is right. Let them assign reports in the review queue to a named colleague, who receives a link to that report's comparison and can approve it without seeing the rest of the migration.

This turns the one bottleneck that does not parallelise into one that does, and it produces a better audit trail: the approval names the person who actually understood the report.

### 4. Guided rewriting for blocked reports

Version 1 tells a customer that a report references a custom assembly and stops. The next step is to show the calling expressions, propose a rewrite in terms the destination supports, and let the user accept, edit or reject it per expression.

The same machinery handles embedded code that calls framework types. It will not solve every case, and it MUST NOT claim to, but turning a hard stop into a scoped task is worth a great deal at the point where a deal is otherwise lost.

### 5. Render performance comparison

While both servers are rendering the same report for validation, record how long each took. Report it alongside the accuracy scores.

It costs almost nothing, since the renders are happening anyway, and it answers a question every customer asks and nobody can currently answer with evidence. If the destination is faster, that is the most persuasive number in the whole migration report.

### 6. Dual-source viewing during the parallel run

During the parallel run, business users have reports in two places and have to know which. A single view listing both estates, marking which reports have moved, removes that from them entirely and makes the parallel run invisible to everyone except the migration team.

### 7. Scheduled and unattended runs

Large estates want the heavy stages to run overnight without anyone watching. Version 1 supports a scheduled window; this extends it to a fully unattended run that reports its result by email, including delta syncs through the parallel run.

### 8. A partner console

Implementation partners migrate several customers a year and currently repeat every step by hand. A console holding several estates, with saved settings and a shared rewrite library, is a modest build that makes partners materially more willing to recommend the product.

### Deliberately not on this list

**Migrating report snapshots and execution history.** Customers ask for it. The right answer is to keep the source server read-only for the retention period, which costs nothing and carries no risk of presenting a reconstructed historical render as the original.

**Automatic deletion on the destination during delta sync.** When a report disappears from the source server, the safe behaviour is to report it, not to delete the destination copy. The cost of being wrong is asymmetric, and it would be the product's first irreversible action.

## Acceptance criteria and testing

### The test estate

Nothing in this section can be verified without a fixture estate, and building it is the first task of the project, not the last. It MUST contain at least one report exercising every classification rule in the transform section, including the blocked ones, plus reports at the size extremes, reports with cascading and multi-value parameters, reports in nested folders five levels deep, a linked report, a report with a subreport, and a report that is already broken on SSRS.

It MUST exist in two forms: a small estate of around 20 reports for the development loop, and a generated estate of 2,000 for load testing.

### Acceptance criteria

Each statement below is verified by an automated end-to-end test against the fixture estate.

| # | Criterion |
| --- | --- |
| 1 | A run against SSRS 2022 detects REST API v2.0 and reports the version to the user |
| 2 | A run against SSRS 2014 detects and uses the SOAP interface, and produces the same final state as the same estate on 2022 |
| 3 | A wrong password produces `SRC-002`; a Kerberos double-hop produces `SRC-004`. The two are never confused |
| 4 | No request issued to the source server during a complete run is a write |
| 5 | Discovery of the fixture estate reports exact counts for folders, data sources, datasets and reports |
| 6 | Every classification rule fires on its fixture report and on no other |
| 7 | A report whose shared dataset cannot be mapped one to one is inlined, and a `TransformAction` row records the change |
| 8 | Upload order is categories, data sources, datasets, reports, with no overlap between stages |
| 9 | Running the same migration twice against the same destination creates no duplicate items |
| 10 | Killing the process during each upload stage and during validation, then resuming, produces the same final state as an uninterrupted run |
| 11 | A report that fails upload three times is marked failed, and the run completes with the remaining reports |
| 12 | `Retry failed only` re-queues failed items and touches nothing already uploaded |
| 13 | Both servers render with an identical parameter set, proven by the stored parameter set hash |
| 14 | A report with identical output scores 100 percent on data accuracy |
| 15 | A report with one deliberately altered value is caught, and the data diff names the differing cell |
| 16 | Differing page counts produce `VALD-003` and no visual score |
| 17 | Removing a font from the destination server triggers the calibration warning before the queue fills |
| 18 | The review queue is ordered worst first, and bulk approval stops at the first report below the threshold |
| 19 | An approval writes an audit row naming the user and the time, and survives staging deletion |
| 20 | No log entry at any level, in any test, contains a password, a token or a complete connection string |
| 21 | The support bundle contains no report definition and no rendered output |
| 22 | Delta sync detects a changed report, a report whose modified date moved without the content changing, and a deleted report, and treats all three correctly |
| 23 | With the shim enabled, a source-form URL with `rs:Format=PDF` and two parameters resolves to the correct destination report in that format |
| 24 | An unmapped path returns the named not-migrated page, not a generic 404 |
| 25 | The staging directory is empty after a run is closed |
| 26 | Every interface string is externalised; no user-visible literal remains in a component |
| 27 | Every control is reachable by keyboard, and progress changes are announced to a screen reader |

### Test layers

| Layer | Covers | Notes |
| --- | --- | --- |
| Unit | Transform rules, classification, redaction, idempotency key generation, URL parameter translation | The redaction tests MUST use realistic connection strings for SQL Server, Oracle and ODBC |
| Contract | Both source adapters against recorded responses | Recordings from real servers of each supported version, refreshed when a version is added |
| Integration | Orchestrator against a live source server and a live destination | The small fixture estate |
| End-to-end | The 27 criteria above | Runs on every release candidate |
| Load | The 2,000-report estate | Measures against the performance targets, and watches memory across a whole stage |
| Interruption | Process kill at each stage boundary and mid-item | Criterion 10 |
| Security | Log scanning for secrets, permission escalation attempts, the iframe authentication path | Criterion 20 |

### Release checklist

- [ ] Brand tokens substituted from the Bold Reports design system, replacing the interim values in the design section
- [ ] Every interface string reviewed against the copy rules
- [ ] A knowledge base article exists for every rule that can block a report, and every link resolves
- [ ] The compatibility rules file validates, and every rule carries an article address
- [ ] Suggested alert definitions shipped and documented
- [ ] Support bundle verified against a real ticket workflow with the support team
- [ ] Load test passed at 2,000 reports
- [ ] Telemetry consent wording reviewed, and refusal verified to cost no function
- [ ] Migration report reviewed by someone who has sat on a change board
- [ ] The existing codebase mapped against this document, with the gaps recorded

### What would make this build fail

Worth naming, because these are the failures that do not show up in a test suite.

**Validation that produces noise.** If visual similarity reports 80 percent on reports that are fine, customers stop believing every number on the screen, including the data accuracy figure that is correct. The font calibration pass is the defence, and it is the first thing likely to be cut.

**A review queue that does not drain.** If the thresholds route most reports to a human, the product has moved the work rather than removed it. If more than 20 percent of uploaded reports land in the queue during testing, the thresholds or the transform engine are wrong, not the customer's estate.

**Error messages written last.** In a self-serve product the messages are the support engineer. Writing them in the final sprint produces the generic strings this document exists to prevent.
