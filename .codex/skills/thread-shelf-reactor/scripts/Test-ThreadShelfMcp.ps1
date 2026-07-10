param()

$ErrorActionPreference = "Stop"
$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot "..\..\..\..")).Path
$mcpProject = Join-Path $repoRoot "ThreadShelf.Mcp\ThreadShelf.Mcp.csproj"
$mcpExe = Join-Path $repoRoot "ThreadShelf.Mcp\bin\Debug\net10.0\ThreadShelf.Mcp.exe"
$codexHomePath = Join-Path ([System.IO.Path]::GetTempPath()) ("threadshelf-mcp-smoke-" + [Guid]::NewGuid().ToString("N"))
$threadId = "55555555-5555-5555-5555-555555555555"

function Send-Rpc([System.Diagnostics.Process]$Process, [hashtable]$Request)
{
    $line = $Request | ConvertTo-Json -Compress -Depth 20
    $bytes = [System.Text.Encoding]::UTF8.GetBytes($line + [Environment]::NewLine)
    $Process.StandardInput.BaseStream.Write($bytes, 0, $bytes.Length)
    $Process.StandardInput.BaseStream.Flush()
    $responseLine = $Process.StandardOutput.ReadLine()
    if ([string]::IsNullOrWhiteSpace($responseLine))
    {
        $stderr = $Process.StandardError.ReadToEnd()
        throw "ThreadShelf MCP returned no response. stderr: $stderr"
    }

    Write-Verbose "MCP response: $responseLine"
    return ($responseLine | ConvertFrom-Json)
}

function Read-ToolData($Response)
{
    return $Response.result.content[0].text | ConvertFrom-Json
}

function Assert-Equal($Actual, $Expected, [string]$Label)
{
    if ($Actual -ne $Expected)
    {
        throw "${Label}: expected '$Expected', got '$Actual'."
    }
}

try
{
    $sessionDirectory = Join-Path $codexHomePath "sessions\2026\07\10"
    New-Item -ItemType Directory -Force -Path $sessionDirectory | Out-Null
    '{"id":"55555555-5555-5555-5555-555555555555","thread_name":"MCP smoke task","updated_at":"2026-07-10T00:00:00Z"}' |
        Set-Content -Encoding utf8 (Join-Path $codexHomePath "session_index.jsonl")
    '{"type":"session_meta","payload":{"cwd":"C:\\ThreadShelf MCP Demo","originator":"fixture","source":"mcp-smoke","timestamp":"2026-07-10T00:00:00Z"}}' |
        Set-Content -Encoding utf8 (Join-Path $sessionDirectory "$threadId.jsonl")

    dotnet build $mcpProject | Out-Host

    $startInfo = [System.Diagnostics.ProcessStartInfo]::new($mcpExe)
    $startInfo.UseShellExecute = $false
    $startInfo.RedirectStandardInput = $true
    $startInfo.RedirectStandardOutput = $true
    $startInfo.RedirectStandardError = $true
    $startInfo.CreateNoWindow = $true
    $startInfo.Environment["CODEX_HOME"] = $codexHomePath
    $startInfo.Environment["THREADSHELF_CODEX_CLI"] = "C:\Windows\System32\where.exe"

    $process = [System.Diagnostics.Process]::new()
    $process.StartInfo = $startInfo
    $process.Start() | Out-Null

    $initialize = Send-Rpc $process @{ jsonrpc = "2.0"; id = 1; method = "initialize"; params = @{} }
    # Windows PowerShell 5.1 can prefix the first redirected stdin write with a UTF-8 BOM.
    # The server correctly reports that one malformed frame; retry once without weakening
    # any of the assertions below. PowerShell 7 and other hosts take the normal path.
    if ($initialize.error.code -eq -32700)
    {
        $initialize = Send-Rpc $process @{ jsonrpc = "2.0"; id = 10; method = "initialize"; params = @{} }
    }
    $catalog = Send-Rpc $process @{ jsonrpc = "2.0"; id = 2; method = "tools/list"; params = @{} }
    $list = Read-ToolData (Send-Rpc $process @{
        jsonrpc = "2.0"; id = 3; method = "tools/call"
        params = @{ name = "threadshelf_list_threads"; arguments = @{ codexHome = $codexHomePath } }
    })
    $unconfirmed = Read-ToolData (Send-Rpc $process @{
        jsonrpc = "2.0"; id = 4; method = "tools/call"
        params = @{
            name = "threadshelf_move_thread"
            arguments = @{ codexHome = $codexHomePath; threadId = $threadId; folder = "AI Sorted" }
        }
    })
    $create = Read-ToolData (Send-Rpc $process @{
        jsonrpc = "2.0"; id = 5; method = "tools/call"
        params = @{
            name = "threadshelf_create_tag"
            arguments = @{
                codexHome = $codexHomePath; name = "ready"; color = "#1F883D"
                description = "Ready for review"; confirmed = $true
            }
        }
    })
    $move = Read-ToolData (Send-Rpc $process @{
        jsonrpc = "2.0"; id = 6; method = "tools/call"
        params = @{
            name = "threadshelf_move_thread"
            arguments = @{ codexHome = $codexHomePath; threadId = $threadId; folder = "AI Sorted"; confirmed = $true }
        }
    })
    $addTag = Read-ToolData (Send-Rpc $process @{
        jsonrpc = "2.0"; id = 7; method = "tools/call"
        params = @{
            name = "threadshelf_add_thread_tag"
            arguments = @{ codexHome = $codexHomePath; threadId = $threadId; tag = "ready"; confirmed = $true }
        }
    })
    $get = Read-ToolData (Send-Rpc $process @{
        jsonrpc = "2.0"; id = 8; method = "tools/call"
        params = @{ name = "threadshelf_get_thread"; arguments = @{ codexHome = $codexHomePath; threadId = $threadId } }
    })
    $native = Read-ToolData (Send-Rpc $process @{
        jsonrpc = "2.0"; id = 9; method = "tools/call"
        params = @{
            name = "threadshelf_archive_thread"
            arguments = @{ codexHome = $codexHomePath; threadId = $threadId; confirmed = $true }
        }
    })

    $process.StandardInput.BaseStream.Close()
    $process.WaitForExit(3000) | Out-Null

    Assert-Equal $initialize.result.serverInfo.name "threadshelf-mcp" "server name"
    Assert-Equal $catalog.result.tools.Count 15 "tool count"
    Assert-Equal $list.source.provider "local-files" "fallback provider"
    Assert-Equal $unconfirmed.error.code "confirmation_required" "confirmation gate"
    Assert-Equal $create.ok $true "tag creation"
    Assert-Equal $move.ok $true "folder move"
    Assert-Equal $addTag.ok $true "tag attachment"
    Assert-Equal $get.data.metadata.folder "AI Sorted" "verified folder"
    Assert-Equal $get.data.metadata.tags[0] "ready" "verified tag"
    Assert-Equal $native.error.code "native_action_unsupported" "fallback native action"

    [pscustomobject]@{
        Server = $initialize.result.serverInfo.name
        ToolCount = $catalog.result.tools.Count
        Provider = $list.source.provider
        ConfirmationError = $unconfirmed.error.code
        VerifiedFolder = $get.data.metadata.folder
        VerifiedTags = @($get.data.metadata.tags)
        NativeError = $native.error.code
        SidecarCreated = Test-Path (Join-Path $codexHomePath "threadshelf\threadshelf.json")
    }
}
finally
{
    if (Get-Variable process -ErrorAction SilentlyContinue)
    {
        if (-not $process.HasExited)
        {
            $process.Kill()
            $process.WaitForExit(3000) | Out-Null
        }
        $process.Dispose()
    }

    $resolvedTemp = [System.IO.Path]::GetFullPath([System.IO.Path]::GetTempPath()).TrimEnd('\')
    $resolvedFixture = [System.IO.Path]::GetFullPath($codexHomePath)
    $isSafeFixture = $resolvedFixture.StartsWith(
        $resolvedTemp + "\threadshelf-mcp-smoke-",
        [StringComparison]::OrdinalIgnoreCase)
    if ($isSafeFixture -and (Test-Path -LiteralPath $resolvedFixture))
    {
        Remove-Item -LiteralPath $resolvedFixture -Recurse -Force
    }
}
