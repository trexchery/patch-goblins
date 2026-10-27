# Fake claude-swap engine for the Patch Goblins agents tests. Synthetic only: no credentials anywhere.
# `run N --require-session -- <claude args>` plays a headless Claude Code worker; prompt markers pick
# its behaviour: [fake:quota] [fake:blocked] [fake:stall] [fake:slow] [fake:hold] [fake:break] [fake:learn] [fake:think]
# [fake:bgwait] [fake:linger] [fake:skipci] [fake:touch=path] [fake:token=path]; a prompt with
# `HANDOFF: PLAN` plays the Director: it answers with the oldest $FAKE_LOG.plan-*.json (used once), else a plan that changes nothing.
$ErrorActionPreference = 'Stop'
[Console]::OutputEncoding = New-Object System.Text.UTF8Encoding($false)
$a = $args
$log = $env:FAKE_LOG
# Concurrent calls share the logs: retry a busy file rather than fail the call over a log line.
function Log($path, $text) {
    for ($i = 0; $i -lt 40; $i++) {
        try { [IO.File]::AppendAllText($path, "$text`r`n"); return } catch { Start-Sleep -Milliseconds 25 }
    }
}
if ($log) { Log $log ($a -join ' ') }

function Emit($o) { [Console]::Out.WriteLine(($o | ConvertTo-Json -Compress -Depth 10)); [Console]::Out.Flush() }

# An Auto-Prime request (`run N … -- -p Hi … --no-session-persistence`): logs which auth variables
# reached it (presence only) and answers like `claude -p --output-format json`. FAKE_PRIME_FAIL refuses it, FAKE_PRIME_CRASH fails it after the launch.
if ($a[0] -eq 'run' -and $a -contains '--no-session-persistence') {
    if ($log) { Log "$log.prime" ("{0}|{1}|{2}|{3}" -f [bool]$env:ANTHROPIC_API_KEY, [bool]$env:CLAUDE_CONFIG_DIR, [bool]$env:CLAUDECODE, (Get-Location).Path) }
    if ($env:FAKE_PRIME_FAIL) { [Console]::Error.WriteLine("Error: $env:FAKE_PRIME_FAIL"); exit 1 } # the engine refused: Claude never started
    [Console]::Out.WriteLine("Launching Account-$($a[1]) (fake) [session mode]")
    if ($env:FAKE_PRIME_CRASH) { [Console]::Error.WriteLine("Error: $env:FAKE_PRIME_CRASH"); exit 1 } # Claude started, then failed
    Emit @{ type = 'result'; subtype = 'success'; is_error = $false; num_turns = 1; result = 'Hello!' }
    exit 0
}

switch ($a[0]) {
    'handoff' {
        $p = Join-Path ([IO.Path]::GetTempPath()) ('fake-patch-goblins-' + [guid]::NewGuid().ToString('N') + '.md')
        Set-Content -LiteralPath $p -Value 'notes from the earlier session'
        Emit @{ path = $p; source = 'claude'; chars = 30 }
        exit 0
    }
    'list' {
        Emit @{ schemaVersion = 1; activeAccountNumber = 1; accounts = @(
            @{ number = 1; email = 'one@example.com'; active = $true; usageStatus = 'ok' },
            @{ number = 2; email = 'two@example.com'; active = $false; usageStatus = 'ok' }) }
        exit 0
    }
    'config' { Emit @{ schemaVersion = 1; settings = @(@{ key = 'autoswitch.threshold'; value = '90' }) }; exit 0 }
    'run' { }
    default { exit 2 }
}

$account = $a[1]
$resume = $a -contains '--resume'
$flag = if ($resume) { '--resume' } else { '--session-id' }
$session = $a[[array]::IndexOf($a, $flag) + 1]
# The prompt file is UTF-8; [Console]::In would decode it with the console's code page under Windows PowerShell 5.1.
$prompt = (New-Object System.IO.StreamReader([Console]::OpenStandardInput(), (New-Object System.Text.UTF8Encoding($false)))).ReadToEnd()
if ($log) {
    Log "$log.prompts" "---- account=$account resume=$resume`n$prompt"
    Log "$log.env" "$env:GH_TOKEN|$env:GIT_ALLOW_PROTOCOL|$env:CLAUDE_CONFIG_DIR"
}
[Console]::Out.WriteLine("Launching Account-$account (fake) [session mode]")
Emit @{ type = 'system'; subtype = 'init'; session_id = $session; model = 'fake-model' }

function Finish([string]$text, [bool]$isError = $false) {
    $sub = if ($isError) { 'error_during_execution' } else { 'success' }
    $linger = $prompt -match '\[fake:linger\]' -and $prompt -notmatch 'You are reviewing'
    # As Claude does with background work: an answer that it still runs first, and an empty result last.
    if ($linger) { Emit @{ type = 'result'; subtype = 'success'; is_error = $false; result = 'The agent is still running in the background.'; session_id = $session } }
    Emit @{ type = 'result'; subtype = $sub; is_error = $isError; result = $text; num_turns = 2; total_cost_usd = 0.01; session_id = $session; duration_ms = 1200 }
    if ($linger) {
        Emit @{ type = 'result'; subtype = 'success'; is_error = $false; result = ''; session_id = $session; duration_ms = 5 }
        Start-Sleep -Seconds 120 # answered, but the process stays: Patch Goblins closes it
    }
    if ($isError) { exit 1 } else { exit 0 }
}

$n = if ($prompt -match 'issue #(\d+)') { $Matches[1] } else { 'request' }
if ($prompt -match 'You are reviewing') {
    $flag = "$log.reviewed-$n"
    if ($prompt -match '\[fake:review-changes\]' -and -not (Test-Path -LiteralPath $flag)) {
        Set-Content -LiteralPath $flag -Value 'x'
        Finish "Read the diff.`nHANDOFF: CHANGES the greeting has no test"
    }
    Finish "Read the diff.`nHANDOFF: APPROVED looks right"
}
if ($prompt -match '\[fake:quota\]' -and $account -eq '2') { Finish "You've hit your session limit, resets 3pm" $true }
if ($prompt -match '\[fake:blocked\]' -and $prompt -notmatch "owner's answer") { Finish "I need a decision first.`nHANDOFF: BLOCKED Should the greeting be formal?" }
if ($prompt -match '\[fake:stall\]' -and -not $resume) { Start-Sleep -Seconds 300 }
if ($prompt -match '\[fake:long\]') {
    for ($k = 0; $k -lt 60; $k++) {
        Emit @{ type = 'assistant'; message = @{ id = "l$k"; content = @(@{ type = 'tool_use'; name = 'Read'; input = @{ file_path = "src/file-$k.cs" } }) } }
        Start-Sleep -Seconds 1
    }
}
if ($prompt -match '\[fake:slow\]') {
    for ($k = 0; $k -lt 10; $k++) {
        Emit @{ type = 'assistant'; message = @{ id = "m$k"; content = @(@{ type = 'text'; text = "step $k" }) } }
        Start-Sleep -Milliseconds 600
    }
}
if ($prompt -match '\[fake:hold\]') {
    # Busy (no stall) until the test creates $FAKE_LOG.release, at most a minute: it outlives whatever the test does meanwhile.
    for ($k = 0; $k -lt 200 -and -not (Test-Path -LiteralPath "$log.release"); $k++) {
        Emit @{ type = 'assistant'; message = @{ id = "h$k"; content = @(@{ type = 'text'; text = "hold $k" }) } }
        Start-Sleep -Milliseconds 300
    }
}
if ($prompt -match '\[fake:think\]') {
    # Reasoning that only streams partial deltas: no message, no tool, no CPU to speak of.
    for ($k = 0; $k -lt 16; $k++) {
        [Console]::Out.WriteLine('{"type":"stream_event","event":{"type":"content_block_delta","index":0,"delta":{"type":"thinking_delta","thinking":"hmm"}}}')
        [Console]::Out.Flush()
        Start-Sleep -Seconds 1
    }
}
if ($prompt -match '\[fake:bgwait\]' -and $prompt -notmatch 'You are reviewing') {
    # A background task Claude waits on, quietly.
    Emit @{ type = 'system'; subtype = 'background_tasks_changed'; tasks = @(@{ task_id = 'b1'; task_type = 'local_bash'; description = 'wait' }) }
    Start-Sleep -Seconds 16
    Emit @{ type = 'system'; subtype = 'background_tasks_changed'; tasks = @() }
}
if ($prompt -match 'HANDOFF: PLAN') {
    $plan = '{"status":"working","summary":"nothing new","items":[]}'
    $next = Get-ChildItem -LiteralPath (Split-Path -Parent $log) -Filter ((Split-Path -Leaf $log) + '.plan-*.json') -ErrorAction SilentlyContinue | Sort-Object Name | Select-Object -First 1
    if ($next) { $plan = Get-Content -Raw -Encoding UTF8 -LiteralPath $next.FullName; Remove-Item -LiteralPath $next.FullName }
    Finish "Read the project.`nHANDOFF: PLAN`n$plan"
}
Emit @{ type = 'assistant'; message = @{ id = 'w1'; content = @(@{ type = 'tool_use'; name = 'Write'; input = @{ file_path = "issue-$n.txt" } }); usage = @{ output_tokens = 42 } } }
if ($prompt -match '\[fake:break\]' -and $prompt -notmatch 'failed') { Set-Content -LiteralPath broken.txt -Value 'x' }
elseif (Test-Path broken.txt) { git rm -q broken.txt | Out-Null }
Add-Content -LiteralPath "issue-$n.txt" -Value "work by account $account"
# [fake:touch=path]: also changes that file (a policy's sensitive path); [fake:skipci]: the commit tells GitHub to skip CI, until told otherwise.
if ($prompt -match '\[fake:touch=([^\]]+)\]') { $t = $Matches[1]; New-Item -ItemType Directory -Force -Path (Split-Path -Parent "./$t") | Out-Null; Add-Content -LiteralPath $t -Value 'x' }
# [fake:token=path]: writes a token-shaped string there (synthetic: AWS's documented example key, split so no scanner takes this file for one).
if ($prompt -match '\[fake:token=([^\]]+)\]') { Add-Content -LiteralPath $Matches[1] -Value ('aws_key = AKIA' + 'IOSFODNN7EXAMPLE') }
$skip = $prompt -match '\[fake:skipci\]' -and $prompt -notmatch 'tells GitHub to skip CI'
if ($prompt -match 'tells GitHub to skip CI') { git reset -q --soft origin/main | Out-Null }
git add -A | Out-Null
git commit -q -m ("fake work for $n" + $(if ($skip) { ' [skip ci]' } else { '' })) | Out-Null
$learned = if ($prompt -match '\[fake:learn\]') { "HANDOFF: LEARNED the build needs the fake toolchain`nHANDOFF: LEARNED tests live next to the code`n" } else { '' }
Finish "Did it.`n${learned}HANDOFF: DONE added issue-$n.txt"
