param(
    [ValidateSet("en-US", "zh-CN")]
    [string]$Language = "en-US"
)

$ErrorActionPreference = "Stop"
$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
$demoHome = Join-Path $repoRoot "artifacts\demo-codex-home"
$demoWorkspaceRoot = Join-Path $repoRoot "artifacts\demo workspaces & 中文"
$atlasWorkspace = Join-Path $demoWorkspaceRoot "Atlas 项目 & One"
$researchWorkspace = Join-Path $demoWorkspaceRoot "研究 Space (Two)"
$fakePublish = Join-Path $repoRoot "artifacts\fake-codex-cli"
$fakeProject = Join-Path $repoRoot "tests\fixtures\ThreadShelf.FakeCodexCli\ThreadShelf.FakeCodexCli.csproj"
$appProject = Join-Path $repoRoot "ThreadShelf.App\ThreadShelf.App.csproj"
$appExe = Join-Path $repoRoot "ThreadShelf.App\bin\x64\Debug\net10.0-windows10.0.22621.0\ThreadShelf.App.exe"

New-Item -ItemType Directory -Force -Path $demoHome | Out-Null
New-Item -ItemType Directory -Force -Path (Join-Path $demoHome "threadshelf") | Out-Null
New-Item -ItemType Directory -Force -Path $atlasWorkspace | Out-Null
New-Item -ItemType Directory -Force -Path $researchWorkspace | Out-Null

$archiveState = Join-Path $demoHome "fake-archive-state.json"
if (Test-Path -LiteralPath $archiveState)
{
    Remove-Item -LiteralPath $archiveState -Force
}
$interactiveLaunchLog = Join-Path $demoHome "fake-interactive-launch.jsonl"
if (Test-Path -LiteralPath $interactiveLaunchLog)
{
    Remove-Item -LiteralPath $interactiveLaunchLog -Force
}

$preferences = [ordered]@{ language = $Language }
$preferences | ConvertTo-Json -Depth 4 | Set-Content -Encoding utf8 (Join-Path $demoHome "threadshelf\preferences.json")

$sidecar = [ordered]@{
    version = 3
    threads = [ordered]@{
        "11111111-1111-1111-1111-111111111111" = [ordered]@{
            folder = "Release"
            tags = @("ready", "docs")
            notes = "Confirm the release notes, package checks, and rollout owner."
            favorite = $true
        }
        "22222222-2222-2222-2222-222222222222" = [ordered]@{
            folder = "Inbox"
            tags = @("bug")
            notes = "Reproduce with the demo identity provider before changing auth code."
            favorite = $false
        }
        "33333333-3333-3333-3333-333333333333" = [ordered]@{
            folder = "References"
            tags = @("docs")
            notes = "Capture the main findings and open questions for the next review."
            favorite = $true
        }
        "44444444-4444-4444-4444-444444444444" = [ordered]@{
            folder = "References"
            tags = @("docs")
            notes = "Synthetic archived task used only for screenshots and UI tests."
            favorite = $false
        }
    }
    tags = [ordered]@{
        bug = [ordered]@{ name = "bug"; color = "#D1242F"; description = "Needs investigation" }
        docs = [ordered]@{ name = "docs"; color = "#8250DF"; description = "Documentation or research" }
        ready = [ordered]@{ name = "ready"; color = "#1F883D"; description = "Ready for the next step" }
    }
    projectAliases = [ordered]@{}
}
$sidecar.projectAliases[$atlasWorkspace] = "Atlas"
$sidecar.projectAliases[$researchWorkspace] = "Research Lab"
$sidecar | ConvertTo-Json -Depth 8 | Set-Content -Encoding utf8 (Join-Path $demoHome "threadshelf\threadshelf.json")

dotnet publish $fakeProject -c Debug -r win-x64 --self-contained false -o $fakePublish
dotnet build $appProject -p:Platform=x64

$env:CODEX_HOME = $demoHome
$env:THREADSHELF_CODEX_CLI = Join-Path $fakePublish "ThreadShelf.FakeCodexCli.exe"
$env:THREADSHELF_FAKE_WORKSPACE_ROOT = $demoWorkspaceRoot
$env:THREADSHELF_FAKE_LAUNCH_LOG = $interactiveLaunchLog
$env:THREADSHELF_TERMINAL = "direct"
$process = Start-Process -FilePath $appExe -WorkingDirectory (Split-Path $appExe) -PassThru

[pscustomobject]@{
    ProcessId = $process.Id
    CodexHome = $demoHome
    FakeCodexCli = $env:THREADSHELF_CODEX_CLI
    WorkspaceRoot = $demoWorkspaceRoot
    InteractiveLaunchLog = $env:THREADSHELF_FAKE_LAUNCH_LOG
}
