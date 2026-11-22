[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$scriptRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
$configPath = Join-Path $scriptRoot 'Configuration.json'

if (Test-Path $configPath) {
    try {
        $config = Get-Content -Path $configPath -Raw | ConvertFrom-Json

        if ($config.PSObject.Properties.Name -contains 'SourceRsUri' -and -not [string]::IsNullOrWhiteSpace($config.SourceRsUri)) {
            $SourceRsUri = $config.SourceRsUri
        }

        if ($config.PSObject.Properties.Name -contains 'FolderPath' -and -not [string]::IsNullOrWhiteSpace($config.FolderPath)) {
            $FolderPath = $config.FolderPath
        }

        if ($config.PSObject.Properties.Name -contains 'BaseUrl' -and -not [string]::IsNullOrWhiteSpace($config.BaseUrl)) {
            $BaseUrl = $config.BaseUrl
        }

        if ($config.PSObject.Properties.Name -contains 'Tenant' -and -not [string]::IsNullOrWhiteSpace($config.Tenant)) {
            $Tenant = $config.Tenant
        }

        if ($config.PSObject.Properties.Name -contains 'BearerToken' -and -not [string]::IsNullOrWhiteSpace($config.BearerToken)) {
            $BearerToken = $config.BearerToken
        }
    }
    catch {
        Write-Warning "Unable to load configuration from '$configPath'. Using built-in defaults. $($_.Exception.Message)"
    }
}

if ([string]::IsNullOrWhiteSpace($BearerToken)) {
    throw 'BearerToken is empty. Update Configuration.json or set it in the script.'
}

$headers = @{
    Authorization = "Bearer $BearerToken"
    'Content-Type' = 'application/json'
}

function Get-ServerTypeFromExtension {
    param([string]$Extension)

    if ([string]::IsNullOrWhiteSpace($Extension)) {
        return 'SQL'
    }

    switch ($Extension.ToUpperInvariant()) {
        'SQL' { return 'SQL' }
        'SQLCE' { return 'SQLCE' }
        'OLEDB' { return 'OLEDB' }
        'ODBC' { return 'ODBC' }
        'ORACLE' { return 'Oracle' }
        'XML' { return 'XML' }
        'SSAS' { return 'SSAS' }
        'POSTGRESQL' { return 'PostgreSQL' }
        'MYSQL' { return 'MySQL' }
        'WEBAPI' { return 'WebApi' }
        default { return 'SQL' }
    }
}

function Get-RelativePath {
    param(
        [Parameter(Mandatory)]
        [string]$BasePath,

        [Parameter(Mandatory)]
        [string]$FullPath
    )

    $normalizedBase = [System.IO.Path]::GetFullPath($BasePath).TrimEnd([char[]]@('\', '/'))
    $normalizedFull = [System.IO.Path]::GetFullPath($FullPath)

    if ($normalizedFull.StartsWith($normalizedBase, [System.StringComparison]::OrdinalIgnoreCase)) {
        return $normalizedFull.Substring($normalizedBase.Length).TrimStart([char[]]@('\', '/'))
    }

    return [System.IO.Path]::GetFileName($FullPath)
}

function Get-DataSourceNameFromReference {
    param( [string]$Reference )

    if ([string]::IsNullOrWhiteSpace($Reference)) {
        return $null
    }

    $name = $Reference.Trim().TrimStart('/') -replace '[\/\s]+', '_'
    return $name.Trim('_')
}

function New-OutputLayout {
    param([string]$RootPath)

    $resolvedRoot = [System.IO.Path]::GetFullPath($RootPath)

    $exportRoot = Join-Path $resolvedRoot 'Export'

    $datasourcesRoot = Join-Path $exportRoot 'Datasources'
    $datasetsRoot = Join-Path $exportRoot 'Datasets'
    $reportsRoot = Join-Path $exportRoot 'Reports'

    $logsRoot = Join-Path $resolvedRoot 'logs'

    foreach ($folder in @($resolvedRoot,$exportRoot,$datasourcesRoot,$datasetsRoot,
                $reportsRoot,$logsRoot ))
    {
        New-Item -ItemType Directory -Path $folder -Force | Out-Null
    }

    foreach ($logFile in @('preflight.log','export.log','datasources.log','datasets.log',
                'reports.log','reconciliation.log' )) 
    {
        New-Item -ItemType File -Path (Join-Path $logsRoot $logFile) -Force | Out-Null
    }

    return [PSCustomObject]@{
        RootPath        = $resolvedRoot
        ExportRoot      = $exportRoot
        DatasourcesRoot = $datasourcesRoot
        DatasetsRoot    = $datasetsRoot
        ReportsRoot     = $reportsRoot
        LogsRoot        = $logsRoot
    }
}

function Write-PhaseLog {
    param( [string]$Message, [string]$LogPath )
    $text = "$(Get-Date -Format 'yyyy-MM-dd HH:mm:ss') | $Message"
    Add-Content -Path $LogPath -Value $text
    Write-Host $text
}

function Initialize-MigrationOutputs {
    param([string]$RootPath)

    $inventoryCsvPath = Join-Path $RootPath 'inventory.csv'
    $itemResultsJsonPath = Join-Path $RootPath 'item-results.json'

    @('Type,Path,Name,Disposition,Count') | Set-Content -Path $inventoryCsvPath -Encoding UTF8
    '{"GeneratedAt":"","TotalItems":0,"Items":[]}' | Set-Content -Path $itemResultsJsonPath -Encoding UTF8

    return [PSCustomObject]@{
        InventoryCsvPath = $inventoryCsvPath
        ItemResultsJsonPath = $itemResultsJsonPath
    }
}

function Update-MigrationInventoryRecord {
    param(
        [hashtable]$InventoryRecordMap,
        [string]$ItemPath,
        [string]$Disposition
    )

    if ([string]::IsNullOrWhiteSpace($ItemPath) -or -not $InventoryRecordMap.ContainsKey($ItemPath)) {
        return
    }

    $InventoryRecordMap[$ItemPath].Disposition = $Disposition
}

function Update-MigrationItemResult {
    param(
        [hashtable]$ItemResultMap,
        [string]$ItemPath,
        [string]$Status,
        [string]$Disposition,
        [string]$Stage,
        [string]$Details,
        [string]$OutputPath,
        [string]$PublishedItemName,
        [string]$PublishedItemId,
        [string]$Error
    )

    if ([string]::IsNullOrWhiteSpace($ItemPath) -or -not $ItemResultMap.ContainsKey($ItemPath)) {
        return
    }

    $result = $ItemResultMap[$ItemPath]
    $result.Status = $Status
    $result.Disposition = $Disposition

    if (-not [string]::IsNullOrWhiteSpace($OutputPath)) {
        $result.OutputPath = $OutputPath
    }

    if (-not [string]::IsNullOrWhiteSpace($PublishedItemName)) {
        $result.PublishedItemName = $PublishedItemName
    }

    if (-not [string]::IsNullOrWhiteSpace($PublishedItemId)) {
        $result.PublishedItemId = $PublishedItemId
    }

    if (-not [string]::IsNullOrWhiteSpace($Error)) {
        $result.Error = $Error
    }

    $result.Stages += [PSCustomObject]@{
        Stage = $Stage
        Status = $Status
        Details = $Details
        Timestamp = (Get-Date).ToString('o')
    }
}

function Write-MigrationInventoryCsv {
    param([object[]]$InventoryRecords, [string]$Path )

    $rows = New-Object System.Collections.Generic.List[object]

    foreach ($record in $InventoryRecords) {
        $rows.Add([PSCustomObject]@{
            Type = $record.Type
            Path = $record.Path
            Name = $record.Name
            Disposition = $record.Disposition
            Count = $record.Count
        })
    }

    $summary = @{}
    foreach ($record in $InventoryRecords) {
        $key = if ([string]::IsNullOrWhiteSpace($record.Type)) { 'Unknown' } else { [string]$record.Type }
        if (-not $summary.ContainsKey($key)) {
            $summary[$key] = 0
        }
        $summary[$key] += [int]$record.Count
    }

    foreach ($entry in ($summary.GetEnumerator() | Sort-Object Key)) {
        $rows.Add([PSCustomObject]@{
            Type = "Summary:$($entry.Key)"
            Path = ''
            Name = ''
            Disposition = 'Count'
            Count = $entry.Value
        })
    }

    $rows | Export-Csv -Path $Path -NoTypeInformation -Encoding UTF8
}

function Write-MigrationItemResultsJson {
    param([object[]]$ItemResults, [string]$Path )

    $payload = [PSCustomObject]@{
        GeneratedAt = (Get-Date).ToString('o')
        TotalItems = @($ItemResults).Count
        Items = @($ItemResults)
    }

    $payload | ConvertTo-Json -Depth 10 | Set-Content -Path $Path -Encoding UTF8
}

# ==========================================
# STEP 1 - EXPORT FROM SSRS
# ==========================================

Write-Host "Exporting content from SSRS..." -ForegroundColor Cyan

if (-not (Get-Command New-RsWebServiceProxy -ErrorAction SilentlyContinue)) {
    throw "ReportingServicesTools module is not available. Install it before running this script."
}

if (Test-Path $FolderPath) {
    Write-Host "Cleaning existing folder..."
    Remove-Item $FolderPath -Recurse -Force
}

$layout = New-OutputLayout -RootPath $FolderPath

$PreflightLog      = Join-Path $layout.LogsRoot "preflight.log"
$ExportLog         = Join-Path $layout.LogsRoot "export.log"
$DatasourceLog     = Join-Path $layout.LogsRoot "datasources.log"
$DatasetLog        = Join-Path $layout.LogsRoot "datasets.log"
$ReportLog         = Join-Path $layout.LogsRoot "reports.log"
$ReconciliationLog = Join-Path $layout.LogsRoot "reconciliation.log"

Write-PhaseLog `
    -Message "Preflight started" `
    -LogPath $PreflightLog

if (-not (Test-Path $layout.RootPath -PathType Container)) {
    throw "Failed to create export folder: $($layout.RootPath)"
}

Write-PhaseLog `
    -Message "Export folder validated: $($layout.RootPath)" `
    -LogPath $PreflightLog

$inventoryOutputs = Initialize-MigrationOutputs -RootPath $layout.RootPath
$inventoryCsvPath = $inventoryOutputs.InventoryCsvPath
$itemResultsJsonPath = $inventoryOutputs.ItemResultsJsonPath

$inventoryRecords = @()
$inventoryRecordMap = @{}
$itemResults = @()
$itemResultMap = @{}
$reportExportPathToSourcePath = @{}
$datasetExportPathToSourcePath = @{}
$datasourceExportFolderToSourcePath = @{}

function Write-LoggedMessage {
    param(
        [Parameter(Mandatory)]
        [AllowNull()]
        [AllowEmptyString()]
        [string]$Message,

        [Parameter(Mandatory)]
        [string]$LogPath
    )

    if ([string]::IsNullOrWhiteSpace($Message)) {
        $Message = '<empty log message>'
    }

    $timestampedMessage = [string]::Format('{0} | {1}', (Get-Date -Format 'yyyy-MM-dd HH:mm:ss'), $Message)
    Add-Content -Path $LogPath -Value $timestampedMessage
    Write-Host $timestampedMessage
}

function Write-LogSuccess {
    param([AllowNull()][AllowEmptyString()][string]$Message)
    Write-LoggedMessage -Message $Message -LogPath $ExportLog
}

function Write-LogError {
    param([AllowNull()][AllowEmptyString()][string]$Message)
    Write-LoggedMessage -Message $Message -LogPath $ExportLog
}

Write-Host 'Export folder created successfully.'

try {
    Write-Host "Connecting to SSRS..."

    if ([string]::IsNullOrWhiteSpace($config.UserName) -or [string]::IsNullOrWhiteSpace($config.Password)) {
        Write-Warning "UserName or Password is empty. Update Configuration.json."

        $proxy = New-RsWebServiceProxy -ReportServerUri $SourceRsUri
    }
    else{
        # SSRS Credentials
        $username = $config.UserName
        $password = ConvertTo-SecureString $config.Password -AsPlainText -Force
        $Credential = New-Object System.Management.Automation.PSCredential ($username, $password)
     
        $proxy = New-RsWebServiceProxy -ReportServerUri $SourceRsUri -Credential $Credential
    }
   Write-LogSuccess "Connected SSRS successfully."

    Write-PhaseLog `
        -Message "Connected to SSRS: $SourceRsUri" `
        -LogPath $PreflightLog

    Write-PhaseLog `
        -Message "Preflight completed successfully" `
        -LogPath $PreflightLog
}
catch {
    throw "Failed to connect to SSRS. Error: $($_.Exception.Message)"
}

Write-Host "Starting export..." -ForegroundColor Cyan

$ErrorLog = Join-Path $FolderPath "ExportErrors.log"

$items = Get-RsFolderContent `
    -Proxy $proxy `
    -RsFolder "/" `
    -Recurse

foreach ($item in $items) {
    $inventoryRecord = [PSCustomObject]@{
        Type = $item.TypeName
        Path = $item.Path
        Name = $item.Name
        Disposition = 'Pending'
        Count = 1
    }

    $inventoryRecords += $inventoryRecord
    $inventoryRecordMap[$item.Path] = $inventoryRecord

    $itemResult = [PSCustomObject]@{
        SourcePath = $item.Path
        SourceType = $item.TypeName
        SourceName = $item.Name
        Status = 'Pending'
        Disposition = 'Pending'
        OutputPath = $null
        PublishedItemName = $null
        PublishedItemId = $null
        Error = $null
        Stages = @()
    }

    $itemResults += $itemResult
    $itemResultMap[$item.Path] = $itemResult
}

foreach ($item in $items)
{
    try
    {
        Write-Host "Processing: $($item.Path) [$($item.TypeName)]"

        $relativePath = $item.Path.TrimStart('/')
        $relativeFolder = Split-Path $relativePath -Parent

        switch ($item.TypeName)
        {
            "Report"
            {
                $bytes = $proxy.GetItemDefinition($item.Path)

                $localFolder = Join-Path `
                    $layout.ReportsRoot `
                    $relativeFolder

                New-Item `
                    -ItemType Directory `
                    -Force `
                    -Path $localFolder | Out-Null

                $fileName = $item.Name

                if ($fileName.EndsWith('.rdl', [System.StringComparison]::OrdinalIgnoreCase)) {
                    $file = Join-Path $localFolder $fileName
                }
                else {
                    $file = Join-Path $localFolder "$fileName.rdl"
                }

                [System.IO.File]::WriteAllBytes($file,$bytes)

                Update-MigrationInventoryRecord -InventoryRecordMap $inventoryRecordMap -ItemPath $item.Path -Disposition 'Exported'
                Update-MigrationItemResult -ItemResultMap $itemResultMap -ItemPath $item.Path -Status 'Exported' -Disposition 'Exported' -Stage 'Export' -Details "Downloaded report RDL to $file" -OutputPath $file

                Write-PhaseLog `
                    -Message "REPORT EXPORTED | $($item.Path)" `
                    -LogPath $ExportLog

                Write-Host "Downloaded RDL: $file" -ForegroundColor Green
            }

            "DataSet"
            {
                $bytes = $proxy.GetItemDefinition($item.Path)

                $localFolder = Join-Path `
                    $layout.DatasetsRoot `
                    $relativeFolder

                New-Item `
                    -ItemType Directory `
                    -Force `
                    -Path $localFolder | Out-Null

                $file = Join-Path $localFolder "$($item.Name).rsd"

                [System.IO.File]::WriteAllBytes($file,$bytes)

                Update-MigrationInventoryRecord -InventoryRecordMap $inventoryRecordMap -ItemPath $item.Path -Disposition 'Exported'
                Update-MigrationItemResult -ItemResultMap $itemResultMap -ItemPath $item.Path -Status 'Exported' -Disposition 'Exported' -Stage 'Export' -Details "Downloaded dataset RSD to $file" -OutputPath $file

                Write-PhaseLog `
                    -Message "DATASET EXPORTED | $($item.Path)" `
                    -LogPath $ExportLog

                Write-Host "Downloaded RSD: $file" -ForegroundColor Green
            }

            "DataSource"
            {
                try
                {
                    $localFolder = Join-Path `
                        $layout.DatasourcesRoot `
                        $relativeFolder

                    if (-not (Test-Path $localFolder))
                    {
                        New-Item `
                            -ItemType Directory `
                            -Path $localFolder `
                            -Force | Out-Null
                    }

                    $parentFolder = (Split-Path $item.Path -Parent).Replace('\','/')

                    if ([string]::IsNullOrWhiteSpace($parentFolder))
                    {
                        $parentFolder = "/"
                    }

                    Out-RsFolderContent `
                        -Proxy $proxy `
                        -RsFolder $parentFolder `
                        -Destination $localFolder `
                        -ErrorAction Stop

                    $datasourceExportFolderToSourcePath[$localFolder] = $item.Path

                    # Keep only RSDS files
                    Get-ChildItem `
                        -Path $localFolder `
                        -File `
                        -Recurse |
                    Where-Object {
                        $_.Extension -ne ".rsds"
                    } |
                    Remove-Item -Force

                    Update-MigrationInventoryRecord -InventoryRecordMap $inventoryRecordMap -ItemPath $item.Path -Disposition 'Exported'
                    Update-MigrationItemResult -ItemResultMap $itemResultMap -ItemPath $item.Path -Status 'Exported' -Disposition 'Exported' -Stage 'Export' -Details "Downloaded datasource files to $localFolder" -OutputPath $localFolder

                    Write-PhaseLog `
                        -Message "DATASOURCE EXPORTED | $($item.Path)" `
                        -LogPath $ExportLog

                    Write-Host "Downloaded RSDS to: $localFolder" -ForegroundColor Green
                }
                catch
                {
                    Write-Warning "Failed DataSource: $($item.Path)"
                    Write-Warning $_.Exception.Message

                    Update-MigrationInventoryRecord -InventoryRecordMap $inventoryRecordMap -ItemPath $item.Path -Disposition 'Failed'
                    Update-MigrationItemResult -ItemResultMap $itemResultMap -ItemPath $item.Path -Status 'Failed' -Disposition 'Failed' -Stage 'Export' -Details $($_.Exception.Message) -Error $($_.Exception.Message)

                    Write-PhaseLog `
                        -Message "DATASOURCE FAILED | $($item.Path) | $($_.Exception.Message)" `
                        -LogPath $ExportLog
                }
            }
        }
    }
    catch
    {
        Write-Warning "Failed: $($item.Path)"
        Write-Warning $_.Exception.Message

        Update-MigrationInventoryRecord -InventoryRecordMap $inventoryRecordMap -ItemPath $item.Path -Disposition 'Failed'
        Update-MigrationItemResult -ItemResultMap $itemResultMap -ItemPath $item.Path -Status 'Failed' -Disposition 'Failed' -Stage 'Export' -Details $($_.Exception.Message) -Error $($_.Exception.Message)

        $errorMessage = [string]::Format('{0} | {1} | {2}', (Get-Date), $item.Path, $_.Exception.Message)
        Add-Content `
            -Path $ErrorLog `
            -Value $errorMessage

        continue
    }
}

Write-Host "Export completed." -ForegroundColor Green

Write-LogSuccess "Export completed."

# ==========================================
# STEP 2 - IMPORT DATASOURCES
# ==========================================

$endDatasourcePoint = "$BaseUrl/reporting/api/site/$Tenant/v1.0/reports/data-sources"
$files = Get-ChildItem -Path $layout.DatasourcesRoot -Filter *.rsds -File -Recurse

if (@($files).Count -eq 0) {
    Write-Warning "No .rsds files found in '$layout.DatasourcesRoot'."
}
else {
    foreach ($file in $files) {
        $relativePath = Get-RelativePath -BasePath $layout.DatasourcesRoot -FullPath $file.FullName

        $folderPath = Split-Path $relativePath -Parent

        $folderName = $folderPath `
            -replace '[\\/]', '_' `
            -replace '\s+', '_'

        $originalDsName = [System.IO.Path]::GetFileNameWithoutExtension($file.Name)
        $originalDsName = $originalDsName -replace '\s+', '_'

        if ([string]::IsNullOrWhiteSpace($folderName))
        {
            $dsName = $originalDsName
        }
        else
        {
            $dsName = "${folderName}_${originalDsName}"
        }

        try {
            $xmlText = Get-Content -LiteralPath $file.FullName -Raw
            [xml]$xml = $xmlText

            $connectNode = $xml.SelectSingleNode('//DataSourceDefinition/ConnectString')
            $extensionNode = $xml.SelectSingleNode('//DataSourceDefinition/Extension')

            if ($connectNode -eq $null -or [string]::IsNullOrWhiteSpace($connectNode.InnerText)) {
                Write-Warning "Skipping '$($file.Name)' - no ConnectString found."
                continue
            }

            $connectString = $connectNode.InnerText.Trim()
            $extension = $null
            if ($extensionNode -ne $null -and -not [string]::IsNullOrWhiteSpace($extensionNode.InnerText)) {
                $extension = $extensionNode.InnerText.Trim()
            }

            # Check for UserName in the RSDS and prompt for a password if present
            $userNode = $xml.SelectSingleNode('//DataSourceDefinition/UserName')
            $userName = $null
            $passwordPlain = $null
            $credentialRetrieval = 'None'

            if ($userNode -ne $null -and -not [string]::IsNullOrWhiteSpace($userNode.InnerText)) {
                $userName = $userNode.InnerText.Trim()
                $credentialRetrieval = 'Store'

                $securePwd = Read-Host -Prompt "Enter password for datasource '$dsName' (user '$userName')" -AsSecureString
                try {
                    $passwordPlain = [Runtime.InteropServices.Marshal]::PtrToStringAuto([Runtime.InteropServices.Marshal]::SecureStringToBSTR($securePwd))
                }
                catch {
                    $passwordPlain = ""
                }

                # Insert or update PassWord node immediately after the UserName node
                $pwdNode = $xml.SelectSingleNode('//DataSourceDefinition/PassWord')
                if ($pwdNode -eq $null) {
                    $pwdNode = $xml.CreateElement('PassWord')
                    $pwdNode.InnerText = $passwordPlain
                    $userNode.ParentNode.InsertAfter($pwdNode, $userNode) | Out-Null
                }
                else {
                    $pwdNode.InnerText = $passwordPlain
                }

                # Save modified RSDS back to disk
                try {
                    $xml.Save($file.FullName)
                }
                catch {
                    Write-Warning "Failed to save updated RSDS '$($file.FullName)': $($_.Exception.Message)"
                }
            }

            $serverType = Get-ServerTypeFromExtension -Extension $extension
            $dsDef = [PSCustomObject]@{
                ConnectString = $connectString
                CredentialRetrieval = $credentialRetrieval
                ImpersonateUser = $false
                WindowsCredentials = $false
                ServerType = $serverType
            }

            if (-not [string]::IsNullOrWhiteSpace($userName)) {
                $dsDef | Add-Member -NotePropertyName UserName -NotePropertyValue $userName
            }

            if (-not [string]::IsNullOrWhiteSpace($passwordPlain)) {
                $dsDef | Add-Member -NotePropertyName Password -NotePropertyValue $passwordPlain
            }

            $payload = [PSCustomObject]@{
                Name = $dsName
                Description = "Auto-created from RSDS file: $($file.Name)"
                DataSourceDefinition = $dsDef
            }

            $bodyJson = $payload | ConvertTo-Json -Depth 8 -Compress
            Write-Host "`nPosting datasource: $dsName (ServerType=$serverType)"

            $sourcePathForDatasource = $null
            foreach ($exportFolderPath in $datasourceExportFolderToSourcePath.Keys) {
                if ($file.FullName.StartsWith($exportFolderPath, [System.StringComparison]::OrdinalIgnoreCase)) {
                    $sourcePathForDatasource = $datasourceExportFolderToSourcePath[$exportFolderPath]
                    break
                }
            }

            $response = Invoke-RestMethod -Method POST -Uri $endDatasourcePoint -Headers $headers -Body $bodyJson -ErrorAction Stop
            if ($response.PSObject.Properties.Name -contains 'Status' -and $response.PSObject.Properties.Name -contains 'ApiStatus') {
                Write-LogSuccess "Created datasource '$dsName'. PublishedItemId: $($response.PublishedItemId)"
                Write-PhaseLog `
                    -Message "CREATED DATASOURCE | $dsName | $($response.PublishedItemId)" `
                    -LogPath $DatasourceLog

                if (-not [string]::IsNullOrWhiteSpace($sourcePathForDatasource)) {
                    Update-MigrationInventoryRecord -InventoryRecordMap $inventoryRecordMap -ItemPath $sourcePathForDatasource -Disposition 'Imported'
                    Update-MigrationItemResult -ItemResultMap $itemResultMap -ItemPath $sourcePathForDatasource -Status 'Imported' -Disposition 'Imported' -Stage 'DatasourceImport' -Details "Created datasource '$dsName'" -PublishedItemName $dsName -PublishedItemId $($response.PublishedItemId)
                }
            }
            else {
                Write-LogError "API returned non-success status for datasource '$dsName'."
                $response | ConvertTo-Json -Depth 6

                if (-not [string]::IsNullOrWhiteSpace($sourcePathForDatasource)) {
                    Update-MigrationInventoryRecord -InventoryRecordMap $inventoryRecordMap -ItemPath $sourcePathForDatasource -Disposition 'ImportWarning'
                    Update-MigrationItemResult -ItemResultMap $itemResultMap -ItemPath $sourcePathForDatasource -Status 'Warning' -Disposition 'ImportWarning' -Stage 'DatasourceImport' -Details "API returned non-success status for datasource '$dsName'"
                }
            }
        }
        catch {
            $httpResp = $_.Exception.Response
            if ($httpResp) {
                $statusCode = $null
                try {
                    $statusCode = [int]$httpResp.StatusCode
                }
                catch {
                }

                if ($statusCode -eq 409) {
                    Write-LogSuccess ('Skipped datasource ''{0}'' because it already exists (409 Conflict).' -f $dsName)

                    Write-PhaseLog `
                        -Message "SKIPPED DATASOURCE | $dsName | Already Exists" `
                        -LogPath $DatasourceLog

                    if (-not [string]::IsNullOrWhiteSpace($sourcePathForDatasource)) {
                        Update-MigrationInventoryRecord -InventoryRecordMap $inventoryRecordMap -ItemPath $sourcePathForDatasource -Disposition 'Skipped'
                        Update-MigrationItemResult -ItemResultMap $itemResultMap -ItemPath $sourcePathForDatasource -Status 'Skipped' -Disposition 'Skipped' -Stage 'DatasourceImport' -Details "Datasource '$dsName' already exists"
                    }

                    continue
                }

                Write-PhaseLog `
                    -Message "FAILED DATASOURCE | $dsName | $($_.Exception.Message)" `
                    -LogPath $DatasourceLog

                if (-not [string]::IsNullOrWhiteSpace($sourcePathForDatasource)) {
                    Update-MigrationInventoryRecord -InventoryRecordMap $inventoryRecordMap -ItemPath $sourcePathForDatasource -Disposition 'Failed'
                    Update-MigrationItemResult -ItemResultMap $itemResultMap -ItemPath $sourcePathForDatasource -Status 'Failed' -Disposition 'Failed' -Stage 'DatasourceImport' -Details $($_.Exception.Message) -Error $($_.Exception.Message)
                }

                $stream = $httpResp.GetResponseStream()
                if ($stream) {
                    $reader = New-Object System.IO.StreamReader($stream)
                    $raw = $reader.ReadToEnd()
                    $reader.Dispose()
                    Write-LogError "Failed to add datasource '$dsName': HTTP $statusCode"
                    Write-LogError "--- Raw Server Response ---"
                    Write-LogError $raw
                }
                else {
                    Write-LogError "Failed to add datasource '$dsName': $($_.Exception.Message)"
                }
            }
            else {
                Write-LogError "Failed to add datasource '$dsName': $($_.Exception.Message)"
            }
        }
    }
}

# ==========================================
# STEP 3 - IMPORT DATASETS
# ==========================================
$endDatasetPoint = "$BaseUrl/reporting/api/site/$Tenant/v1.0/reports/datasets"
$files = Get-ChildItem -Path $layout.DatasetsRoot -Filter *.rsd -File -Recurse

if (@($files).Count -eq 0) {
    Write-Warning "No .rsd files found in '$layout.DatasetsRoot'."
}
else {
    foreach ($file in $files) {
        $relativePath = Get-RelativePath -BasePath $layout.DatasetsRoot -FullPath $file.FullName

        $folderPath = Split-Path $relativePath -Parent

        $folderName = $folderPath `
            -replace '[\\/]', '_' `
            -replace '\s+', '_'

        $originalDsName = [System.IO.Path]::GetFileNameWithoutExtension($file.Name)
        $originalDsName = $originalDsName -replace '\s+', '_'

        if ([string]::IsNullOrWhiteSpace($folderName))
        {
            $datasetName = $originalDsName
        }
        else
        {
            $datasetName = "${folderName}_${originalDsName}"
        }

        try {
            $xmlText = Get-Content -LiteralPath $file.FullName -Raw
            [xml]$xml = $xmlText

            $namespaceManager = New-Object System.Xml.XmlNamespaceManager($xml.NameTable)
            $namespaceManager.AddNamespace('sds', 'http://schemas.microsoft.com/sqlserver/reporting/2010/01/shareddatasetdefinition')

            $dsRefNode = $xml.SelectSingleNode('/sds:SharedDataSet/sds:DataSet/sds:Query/sds:DataSourceReference', $namespaceManager)
            if ($dsRefNode -eq $null -or [string]::IsNullOrWhiteSpace($dsRefNode.InnerText)) {
                Write-Warning "Skipping '$($file.Name)' - no DataSourceReference found."
                continue
            }

            $value = $dsRefNode.InnerText.Trim().TrimStart('/')

            $parts = $value -split '/' | Where-Object {-not [string]::IsNullOrWhiteSpace($_)}

            $dataSourceName = ($parts -join '_') -replace '\s+','_'

            $dsRefNode.InnerText = $dataSourceName
            $xml.Save($file.FullName)

            Write-Host "Updated '$($file.Name)' : $dataSourceName" -ForegroundColor Green

            $bytes = [System.IO.File]::ReadAllBytes($file.FullName)
            $itemContentBase64 = [System.Convert]::ToBase64String($bytes)

            $payload = [PSCustomObject]@{
                Name = $datasetName
                Description = "Imported from file: $($file.Name)"
                DataSourceMappingInfo = @(
                    [PSCustomObject]@{
                        Name = $dataSourceName
                        DataSourceName = $dataSourceName
                    }
                )
                ItemContent = $itemContentBase64
            }

            $bodyJson = $payload | ConvertTo-Json -Depth 8 -Compress
            Write-Host "`nPosting dataset: $datasetName (DataSourceName=$dataSourceName)"

            $sourcePathForDataset = $null
            if ($datasetExportPathToSourcePath.ContainsKey($file.FullName)) {
                $sourcePathForDataset = $datasetExportPathToSourcePath[$file.FullName]
            }

            $response = Invoke-RestMethod -Method POST -Uri $endDatasetPoint -Headers $headers -Body $bodyJson -ErrorAction Stop
            if ($response.PSObject.Properties.Name -contains 'Status' -and $response.PSObject.Properties.Name -contains 'ApiStatus') {
                Write-LogSuccess "Created dataset '$datasetName'. PublishedItemId: $($response.PublishedItemId)"

                Write-PhaseLog `
                -Message "CREATED DATASET | $datasetName | $($response.PublishedItemId)" `
                -LogPath $DatasetLog

                if (-not [string]::IsNullOrWhiteSpace($sourcePathForDataset)) {
                    Update-MigrationInventoryRecord -InventoryRecordMap $inventoryRecordMap -ItemPath $sourcePathForDataset -Disposition 'Imported'
                    Update-MigrationItemResult -ItemResultMap $itemResultMap -ItemPath $sourcePathForDataset -Status 'Imported' -Disposition 'Imported' -Stage 'DatasetImport' -Details "Created dataset '$datasetName'" -PublishedItemName $datasetName -PublishedItemId $($response.PublishedItemId)
                }
            }
            else {
                Write-LogError "API returned non-success status for dataset '$datasetName'."
                $response | ConvertTo-Json -Depth 6

                if (-not [string]::IsNullOrWhiteSpace($sourcePathForDataset)) {
                    Update-MigrationInventoryRecord -InventoryRecordMap $inventoryRecordMap -ItemPath $sourcePathForDataset -Disposition 'ImportWarning'
                    Update-MigrationItemResult -ItemResultMap $itemResultMap -ItemPath $sourcePathForDataset -Status 'Warning' -Disposition 'ImportWarning' -Stage 'DatasetImport' -Details "API returned non-success status for dataset '$datasetName'"
                }
            }
        }
        catch {
            $httpResp = $null
            if ($_.Exception -and $_.Exception.PSObject.Properties['Response']) {
                $httpResp = $_.Exception.Response
            }

            if ($httpResp) {
                $statusCode = $null
                try {
                    $statusCode = [int]$httpResp.StatusCode
                }
                catch {
                }

                $stream = $httpResp.GetResponseStream()
                if ($stream) {
                    $reader = New-Object System.IO.StreamReader($stream)
                    $raw = $reader.ReadToEnd()
                    $reader.Dispose()
                    if ($statusCode -eq 409) {
                        Write-LogSuccess ('Skipped dataset ''{0}'' because it already exists (409 Conflict).' -f $datasetName)

                        Write-PhaseLog `
                        -Message "SKIPPED DATASET | $datasetName | Already Exists" `
                        -LogPath $DatasetLog

                        if (-not [string]::IsNullOrWhiteSpace($sourcePathForDataset)) {
                            Update-MigrationInventoryRecord -InventoryRecordMap $inventoryRecordMap -ItemPath $sourcePathForDataset -Disposition 'Skipped'
                            Update-MigrationItemResult -ItemResultMap $itemResultMap -ItemPath $sourcePathForDataset -Status 'Skipped' -Disposition 'Skipped' -Stage 'DatasetImport' -Details "Dataset '$datasetName' already exists"
                        }

                        continue
                    }

                    Write-PhaseLog `
                    -Message "FAILED DATASET | $datasetName | $($_.Exception.Message)" `
                    -LogPath $DatasetLog

                    if (-not [string]::IsNullOrWhiteSpace($sourcePathForDataset)) {
                        Update-MigrationInventoryRecord -InventoryRecordMap $inventoryRecordMap -ItemPath $sourcePathForDataset -Disposition 'Failed'
                        Update-MigrationItemResult -ItemResultMap $itemResultMap -ItemPath $sourcePathForDataset -Status 'Failed' -Disposition 'Failed' -Stage 'DatasetImport' -Details $($_.Exception.Message) -Error $($_.Exception.Message)
                    }

                    Write-LogError "Failed to add dataset '$datasetName': HTTP $statusCode"
                    Write-LogError "--- Raw Server Response ---"
                    Write-LogError $raw
                }
                else {
                    Write-LogError "Failed to add dataset '$datasetName': $($_.Exception.Message)"
                }
            }
            else {
                Write-LogError "Failed to add dataset '$datasetName': $($_.Exception.Message)"
            }
        }
    }
}

# ==========================================
# STEP 4 - IMPORT REPORTS
# ==========================================

# #---------- Endpoints --------------------------------------------

$siteBase = "$($BaseUrl.TrimEnd('/'))/reporting/api/site/$Tenant"  
$listCategoriesEndpoint = "$siteBase/v1.0/items?ItemType=Category"	#GET 
$categoriesEndpoint = "$siteBase/v5.0/categories"					#POST
$reportsEndpoint = "$siteBase/v1.0/reports"							#POST

function Get-BoldCategoryId {
    param(
        [string]$CategoryName,
        [string]$ParentId = $null
    )

    if ([string]::IsNullOrWhiteSpace($ParentId))
    {
        $ParentId = $null
    }
    $cacheKey = "$ParentId|$CategoryName"

    if ($script:CategoryCache.ContainsKey($cacheKey)) {
        return $script:CategoryCache[$cacheKey]
    }

    try {
        $allCategories = Invoke-RestMethod -Method Get -Uri $listCategoriesEndpoint -Headers $headers
    }
    catch {
        throw "Unable to fetch category list from server: $($_.Exception.Message)"
    }

    foreach ($cat in $allCategories) {
        $catParentId = $null
        if ($cat.PSObject.Properties['ParentId'])
        {
        $catParentId = $cat.ParentId
        }

        $key = "$catParentId|$($cat.Name)"
        $script:CategoryCache[$key] = $cat.Id

        if ($cat.Name.Trim().ToLower() -eq $CategoryName.Trim().ToLower() -and
            [string]$catParentId -eq [string]$ParentId )
        {
            return $cat.Id
        }
    }

    $newCatBody = @{
        Name = $CategoryName
        Description = "Auto-created during import"
        ParentId = $ParentId
    } | ConvertTo-Json -Depth 3

    try {
        $created = Invoke-RestMethod -Method Post -Uri $categoriesEndpoint -Headers $headers -Body $newCatBody

        if( $created.PSObject.Properties['Id']) {
            $script:CategoryCache[$cacheKey] = $created.Id
            Write-Host "Created category '$CategoryName'" -ForegroundColor Green
            return $created.Id
        }
       
        if( $created.PSObject.Properties['Data']) {
            $script:CategoryCache[$cacheKey] = $created.Data
            Write-Host "Created category '$CategoryName'" -ForegroundColor Green
            return $created.Data
        }       
    }
    catch {
        Write-LogError "Failed to create category '$CategoryName': $($_.Exception.Message)"
        continue
    }
}

#---------- MAIN -------------------------------------------------

$script:CategoryCache = @{}
Write-Host "Scanning for RDL files in $layout.ReportsRoot ..." -ForegroundColor Cyan
$rdlFiles = Get-ChildItem -Path $layout.ReportsRoot -Recurse -Filter *.rdl

if (-not $rdlFiles) {
    Write-Warning 'No RDL files were found - nothing to upload.'
}
else {
    foreach ($file in $rdlFiles) {
        Write-Host ''
        Write-Host "Processing $($file.Name)" -ForegroundColor Cyan

        $relativePath = Get-RelativePath -BasePath $layout.ReportsRoot -FullPath $file.FullName
        $pathParts = $relativePath -split '[\\/]'

        if ($pathParts.Count -gt 1)
        {
            # All folders excluding the report name
            $folders = $pathParts | Select-Object -SkipLast 1

            $ParentId = $null
            $categoryName = $null

            foreach ($folder in $folders)
            {
                Write-Host "Creating Category '$folder' under Parent '$ParentId'"
                $ParentId = Get-BoldCategoryId -CategoryName $folder -ParentId $ParentId
                $categoryName = $folder
            }
            
            $categoryId = $ParentId
        }
        else
        {
            $categoryName = "Sample Reports"
            $categoryId = Get-BoldCategoryId -CategoryName $categoryName
        }

        [xml]$rdlXml = Get-Content -LiteralPath $file.FullName -Raw

		# -----------------------------
		# DATASOURCE MAPPINGS (shared)
		# -----------------------------

        $dataSourceMappings = @()
        $dsNodes = $rdlXml.SelectNodes("//*[local-name()='DataSource']")

        foreach ($dsNode in $dsNodes) {
            $rdlDataSourceName = $dsNode.GetAttribute('Name')
            $refNode = $dsNode.SelectSingleNode('./*[local-name()=''DataSourceReference'']')

            if ($refNode -eq $null) {
                continue
            }

            $ref = $refNode.InnerText.Trim()
            if ([string]::IsNullOrWhiteSpace($ref)) {
                continue
            }

            $publishedDataSourceName = Get-DataSourceNameFromReference -Reference $ref
            if ([string]::IsNullOrWhiteSpace($publishedDataSourceName)) {
                continue
            }

            $dataSourceMappings += [PSCustomObject]@{
                Name = $rdlDataSourceName
                DataSourceName = $publishedDataSourceName
            }
        }

        $dataSourceMappings = $dataSourceMappings | Sort-Object Name -Unique

		# -----------------------------
		# DATASET MAPPINGS (shared)
		# -----------------------------

        $datasetMappings = @()
        $dsNodes = $rdlXml.SelectNodes("//*[local-name()='DataSet']")

        foreach ($dsNode in $dsNodes) {
            $rdlDataSetName = $dsNode.GetAttribute('Name')
            $refNode = $dsNode.SelectSingleNode('.//*[local-name()=''SharedDataSetReference'']')

            if ($refNode -eq $null) {
                continue
            }

            $ref = $refNode.InnerText.Trim()
            if ([string]::IsNullOrWhiteSpace($ref)) {
                continue
            }

            $publishedDataSetName = Get-DataSourceNameFromReference -Reference $ref
            if ([string]::IsNullOrWhiteSpace($publishedDataSetName)) {
                continue
            }

            $datasetMappings += [PSCustomObject]@{
                Name = $rdlDataSetName
                DataSetName = $publishedDataSetName
            }
        }

        $datasetMappings = $datasetMappings | Sort-Object Name -Unique

        $dsNodesAll = $rdlXml.SelectNodes("//*[local-name()='DataSource']")

        foreach ($dsNode2 in $dsNodesAll)
        {
            $securityNode = $dsNode2.SelectSingleNode("./*[local-name()='SecurityType']")

            if ($securityNode -ne $null -and $securityNode.InnerText -eq "DataBase")
            {
                $connProps = $dsNode2.SelectSingleNode("./*[local-name()='ConnectionProperties']")
                if ($connProps -eq $null) { continue }

                $dataSourceName = $dsNode2.GetAttribute("Name")

                Write-Host ""
                Write-Host "Enter credentials for DataSource: $dataSourceName"

                $dsUser = Read-Host "UserName"
                $securePwd = Read-Host "Password" -AsSecureString
                $dsPass = [System.Net.NetworkCredential]::new("", $securePwd).Password

                $xmlDoc = $connProps.OwnerDocument
                $ns = $xmlDoc.DocumentElement.NamespaceURI

                # UserName
                $userNode = $connProps.SelectSingleNode("./*[local-name()='UserName']")
                if ($userNode -eq $null)
                {
                    $userNode = $xmlDoc.CreateElement("UserName", $ns)
                    $userNode.InnerText = $dsUser
                    [void]$connProps.AppendChild($userNode)
                }
                else
                {
                    $userNode.InnerText = $dsUser
                }

                # PassWord
                $passNode = $connProps.SelectSingleNode("./*[local-name()='PassWord']")
                if ($passNode -eq $null)
                {
                    $passNode = $xmlDoc.CreateElement("PassWord", $ns)
                    $passNode.InnerText = $dsPass
                    [void]$connProps.AppendChild($passNode)
                }
                else
                {
                    $passNode.InnerText = $dsPass
                }

                Write-Host "Updated DataSource '$dataSourceName'" -ForegroundColor Green
            }
        }

        try {
            $xmlContent = $rdlXml.OuterXml
            $itemB64 = [Convert]::ToBase64String([System.Text.Encoding]::UTF8.GetBytes($xmlContent))
        }
        catch {
            Write-Warning "Could not read '$($file.FullName)': $($_.Exception.Message)"
            continue
        }

        $bodyObj = @{
            Name = $file.BaseName
            Description = "Imported from SSRS ($relativePath)"
            CategoryId = $categoryId
            Tags = @()
            ServerPath = ''
            IsPublic = $false
            DataSetMappingInfo = @($datasetMappings)
            DataSourceMappingInfo = @($dataSourceMappings)
            ItemContent = $itemB64
        }

        $jsonBody = $bodyObj | ConvertTo-Json -Depth 10

         try {
            $sourcePathForReport = $null
            if ($reportExportPathToSourcePath.ContainsKey($file.FullName)) {
                $sourcePathForReport = $reportExportPathToSourcePath[$file.FullName]
            }

            Invoke-RestMethod -Method Post -Uri $reportsEndpoint -Headers $headers -Body $jsonBody | Out-Null
            
            Write-LogSuccess "Uploaded report '$($file.BaseName)' to category '$categoryName'"

            Write-PhaseLog `
            -Message "CREATED REPORT | $($file.BaseName) | Category=$categoryName" `
            -LogPath $ReportLog

            if (-not [string]::IsNullOrWhiteSpace($sourcePathForReport)) {
                Update-MigrationInventoryRecord -InventoryRecordMap $inventoryRecordMap -ItemPath $sourcePathForReport -Disposition 'Imported'
                Update-MigrationItemResult -ItemResultMap $itemResultMap -ItemPath $sourcePathForReport -Status 'Imported' -Disposition 'Imported' -Stage 'ReportImport' -Details "Uploaded report '$($file.BaseName)' to category '$categoryName'" -PublishedItemName $file.BaseName
            }
        }
        catch {
            $resp = $null
            if ($_.Exception -and $_.Exception.PSObject.Properties['Response']) {
                $resp = $_.Exception.Response
            }

            if ($resp -and $resp.StatusCode.value__ -eq 409) {
                Write-Host "Report '$($file.BaseName)' already exists - skipping." -ForegroundColor Yellow

                Write-PhaseLog `
                -Message "SKIPPED REPORT | $($file.BaseName) | Already Exists" `
                -LogPath $ReportLog
                continue

                if (-not [string]::IsNullOrWhiteSpace($sourcePathForReport)) {
                    Update-MigrationInventoryRecord -InventoryRecordMap $inventoryRecordMap -ItemPath $sourcePathForReport -Disposition 'Skipped'
                    Update-MigrationItemResult -ItemResultMap $itemResultMap -ItemPath $sourcePathForReport -Status 'Skipped' -Disposition 'Skipped' -Stage 'ReportImport' -Details "Report '$($file.BaseName)' already exists"
                }
                
            }
            else {
                Write-LogError "Failed to upload report '$($file.BaseName)': $($_.Exception.Message)"

                Write-PhaseLog `
                -Message "FAILED REPORT | $($file.BaseName) | $($_.Exception.Message)" `
                -LogPath $ReportLog
                continue

                if (-not [string]::IsNullOrWhiteSpace($sourcePathForReport)) {
                    Update-MigrationInventoryRecord -InventoryRecordMap $inventoryRecordMap -ItemPath $sourcePathForReport -Disposition 'Failed'
                    Update-MigrationItemResult -ItemResultMap $itemResultMap -ItemPath $sourcePathForReport -Status 'Failed' -Disposition 'Failed' -Stage 'ReportImport' -Details $($_.Exception.Message) -Error $($_.Exception.Message)
                }
                
            }
        }
    }
}

$migrationSummary = Join-Path $layout.RootPath "migration-summary.md"

@"
# SSRS Migration Summary

Completed : $(Get-Date)

Export Folder:
$($layout.ExportRoot)

Logs:
 - preflight.log
 - export.log
 - datasources.log
 - datasets.log
 - reports.log
 - reconciliation.log

Status:
Migration completed successfully.
"@ | Set-Content $migrationSummary

$datasourceCount =
(
    Get-ChildItem `
        -Path $layout.DatasourcesRoot `
        -Filter *.rsds `
        -Recurse |
    Measure-Object
).Count

$datasetCount =
(
    Get-ChildItem `
        -Path $layout.DatasetsRoot `
        -Filter *.rsd `
        -Recurse |
    Measure-Object
).Count

$reportCount =
(
    Get-ChildItem `
        -Path $layout.ReportsRoot `
        -Filter *.rdl `
        -Recurse |
    Measure-Object
).Count


Write-PhaseLog `
    -Message "Datasource Files : $datasourceCount" `
    -LogPath $ReconciliationLog

Write-PhaseLog `
    -Message "Dataset Files : $datasetCount" `
    -LogPath $ReconciliationLog

Write-PhaseLog `
    -Message "Report Files : $reportCount" `
    -LogPath $ReconciliationLog

Write-MigrationInventoryCsv -InventoryRecords $inventoryRecords -Path $inventoryCsvPath
Write-MigrationItemResultsJson -ItemResults $itemResults -Path $itemResultsJsonPath

Write-LogSuccess 'MIGRATION COMPLETED SUCCESSFULLY'
Write-Host ''
Write-Host '===================================' -ForegroundColor Cyan
Write-Host 'MIGRATION COMPLETED SUCCESSFULLY' -ForegroundColor Green
Write-Host '===================================' -ForegroundColor Cyan
