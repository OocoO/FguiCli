param(
    [Parameter(Mandatory = $true)]
    [string]$ProjectRootDir,

    [Parameter(Mandatory = $true)]
    [string]$PackageName,

    [Parameter(Mandatory = $true)]
    [string]$ComponentName,

    [Parameter(Mandatory = $true)]
    [string]$OutPng,

    [string]$BranchTag = "",
    [int]$Width = 1920,
    [int]$Height = 1080,
    [int]$TimeoutSec = 120,
    [string]$Server = "http://127.0.0.1:18765"
)

$payload = @{
    projectRootDir = $ProjectRootDir
    packageName = $PackageName
    componentName = $ComponentName
    outPng = $OutPng
    branchTag = $BranchTag
    width = $Width
    height = $Height
    timeoutSec = $TimeoutSec
} | ConvertTo-Json

Invoke-RestMethod -Method Post -Uri ($Server.TrimEnd('/') + '/render_page') -ContentType 'application/json' -Body $payload

