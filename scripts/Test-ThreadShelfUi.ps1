param(
    [string]$AppExe = "",
    [switch]$KeepOpen
)

$ErrorActionPreference = "Stop"
$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
if ([string]::IsNullOrWhiteSpace($AppExe))
{
    $AppExe = Join-Path $repoRoot "ThreadShelf.App\bin\publish\win-x64\ThreadShelf.App.exe"
}

if (-not (Get-Command winapp -ErrorAction SilentlyContinue))
{
    throw "winapp is required for ThreadShelf UI automation."
}

if (-not (Test-Path -LiteralPath $AppExe -PathType Leaf))
{
    throw "ThreadShelf executable not found: $AppExe. Publish WinX64NativeAot first."
}

function Invoke-WinAppJson
{
    param(
        [Parameter(Mandatory)]
        [string[]]$Arguments,
        [switch]$AllowFailure
    )

    $previousPreference = $ErrorActionPreference
    $ErrorActionPreference = "Continue"
    $output = & winapp ui @Arguments 2>&1
    $exitCode = $LASTEXITCODE
    $ErrorActionPreference = $previousPreference
    if ($exitCode -ne 0 -and -not $AllowFailure)
    {
        throw "winapp ui $($Arguments -join ' ') failed ($exitCode): $($output -join [Environment]::NewLine)"
    }

    $json = ($output | ForEach-Object { "$_" }) -join [Environment]::NewLine
    if ([string]::IsNullOrWhiteSpace($json))
    {
        return $null
    }

    return $json | ConvertFrom-Json
}

function Assert-Ui
{
    param(
        [Parameter(Mandatory)]
        [bool]$Condition,
        [Parameter(Mandatory)]
        [string]$Message
    )

    if (-not $Condition)
    {
        throw $Message
    }
}

$demoOutput = & (Join-Path $PSScriptRoot "Start-ThreadShelfDemo.ps1") -Language en-US
$demoProcessId = @($demoOutput.ProcessId) | Select-Object -Last 1
if ($demoProcessId)
{
    Stop-Process -Id $demoProcessId -Force -ErrorAction SilentlyContinue
    Wait-Process -Id $demoProcessId -ErrorAction SilentlyContinue
}

$resolvedAppExe = (Resolve-Path -LiteralPath $AppExe).Path
$appProcess = Start-Process `
    -FilePath $resolvedAppExe `
    -WorkingDirectory (Split-Path $resolvedAppExe) `
    -PassThru

try
{
    $status = $null
    for ($attempt = 0; $attempt -lt 50 -and $null -eq $status; $attempt++)
    {
        Start-Sleep -Milliseconds 100
        try
        {
            $status = Invoke-WinAppJson -Arguments @(
                "status", "-a", "$($appProcess.Id)", "--json")
        }
        catch
        {
            $status = $null
        }
    }
    Assert-Ui ($null -ne $status) "ThreadShelf window did not become available to UI Automation."

    Invoke-WinAppJson -Arguments @(
        "wait-for",
        "ResumeThreadButton_11111111_1111_1111_1111_111111111111",
        "-a", "$($appProcess.Id)",
        "-t", "10000",
        "--json") | Out-Null
    Invoke-WinAppJson -Arguments @(
        "wait-for", "WorkspaceFolderLink", "-a", "$($appProcess.Id)", "-t", "10000", "--json") | Out-Null
    Invoke-WinAppJson -Arguments @(
        "get-property",
        "ResumeThreadButton_11111111_1111_1111_1111_111111111111",
        "-a", "$($appProcess.Id)",
        "--json") | Out-Null
    Invoke-WinAppJson -Arguments @(
        "get-property", "WorkspaceFolderLink", "-a", "$($appProcess.Id)", "--json") | Out-Null

    Invoke-WinAppJson -Arguments @(
        "invoke",
        "Row[1]=22222222-2222-2222-2222-222222222222",
        "-a", "$($appProcess.Id)",
        "--json") | Out-Null

    $title = Invoke-WinAppJson -Arguments @(
        "get-value", "ThreadTitleTextBox", "-a", "$($appProcess.Id)", "--json")
    Assert-Ui ($title.text -eq "Investigate sign-in regression") `
        "Selecting the second task did not update the details pane."

    $cardResume = Invoke-WinAppJson -Arguments @(
        "get-property",
        "ResumeThreadButton_22222222_2222_2222_2222_222222222222",
        "-a", "$($appProcess.Id)",
        "--json")
    $detailsResume = Invoke-WinAppJson -Arguments @(
        "get-property", "ResumeThreadButton", "-a", "$($appProcess.Id)", "--json")
    foreach ($resume in @($cardResume, $detailsResume))
    {
        Assert-Ui ($resume.properties.IsEnabled -eq "True") "A Resume entry is unexpectedly disabled."
        Assert-Ui ($resume.properties.Name -eq "Resume task Investigate sign-in regression in Codex") `
            "A Resume entry does not use provider-neutral automation text."
    }

    $workspace = Invoke-WinAppJson -Arguments @(
        "get-property", "WorkspaceFolderLink", "-a", "$($appProcess.Id)", "--json")
    Assert-Ui ($workspace.properties.IsEnabled -eq "True") "The valid workspace link is disabled."
    Assert-Ui ($workspace.properties.Name -like "Open workspace folder *Atlas*") `
        "The workspace link automation name is missing its action or path."

    $duplicateOpen = Invoke-WinAppJson -Arguments @(
        "search", "OpenInCodexButton", "-a", "$($appProcess.Id)", "--json") -AllowFailure
    Assert-Ui ($duplicateOpen.matchCount -eq 0) "The removed duplicate Open button is still present."

    $menuReady = $null
    for ($attempt = 0; $attempt -lt 3 -and -not $menuReady.found; $attempt++)
    {
        Invoke-WinAppJson -Arguments @(
            "click", "Project Atlas (2)", "--right", "-a", "$($appProcess.Id)", "--json") | Out-Null
        $menuReady = Invoke-WinAppJson -Arguments @(
            "wait-for", "New task", "-a", "$($appProcess.Id)", "-t", "3000", "--json") -AllowFailure
    }
    Assert-Ui ($menuReady.found) "The project New task menu item did not appear after right-click."
    $menuSearch = Invoke-WinAppJson -Arguments @(
        "search", "New task", "-a", "$($appProcess.Id)", "--json")
    $menuItems = @(
        @($menuSearch.matches) |
            Where-Object { $_.type -eq "MenuItem" -and $_.name -eq "New task" })
    Assert-Ui ($menuItems.Count -eq 1) "The project New task menu item was not found."
    Assert-Ui ($menuItems[0].isEnabled) "The project New task menu item is unexpectedly disabled."

    [pscustomobject]@{
        ProcessId = $appProcess.Id
        Executable = $resolvedAppExe
        CardResume = "passed"
        DetailsResume = "passed"
        WorkspaceLink = "passed"
        ProjectNewTask = "passed"
        DuplicateOpenRemoved = "passed"
    }
}
finally
{
    if (-not $KeepOpen -and -not $appProcess.HasExited)
    {
        Stop-Process -Id $appProcess.Id -Force -ErrorAction SilentlyContinue
        Wait-Process -Id $appProcess.Id -ErrorAction SilentlyContinue
    }
}
