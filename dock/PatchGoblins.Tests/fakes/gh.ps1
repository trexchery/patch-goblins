# Fake GitHub CLI for the Patch Goblins agents tests: issues, pull requests and checks come from JSON files
# in $env:FAKE_GH, and every call is logged there. Nothing reaches GitHub.
$ErrorActionPreference = 'Stop'
[Console]::OutputEncoding = New-Object System.Text.UTF8Encoding($false)
$a = $args
$dir = $env:FAKE_GH
# Concurrent calls share the logs: retry a busy file rather than fail the call over a log line.
function Log($path, $text) {
    for ($i = 0; $i -lt 40; $i++) {
        try { [IO.File]::AppendAllText($path, "$text`r`n"); return } catch { Start-Sleep -Milliseconds 25 }
    }
}
Log (Join-Path $dir 'gh.log') ($a -join ' ')

function Read-Text($name, $default) {
    $p = Join-Path $dir $name
    if (Test-Path -LiteralPath $p) { Get-Content -Raw -LiteralPath $p } else { $default }
}
function Arg($name) { $i = [array]::IndexOf($a, $name); if ($i -ge 0) { $a[$i + 1] } }
function Prs() { @(Read-Text 'prs.json' '[]' | ConvertFrom-Json) }

# No network: every call fails the way gh does offline.
if (Test-Path -LiteralPath (Join-Path $dir 'offline')) { [Console]::Error.WriteLine('error connecting to api.github.com'); exit 1 }
switch ("$($a[0]) $($a[1])") {
    'api user' { 'owner'; exit 0 }
    'issue list' { Read-Text 'issues.json' '[]'; exit 0 }
    'issue comment' { Copy-Item -LiteralPath (Arg '--body-file') -Destination (Join-Path $dir "question-$($a[2]).md"); exit 0 }
    'issue view' {
        if ((Arg '--json') -eq 'comments') { Read-Text "comments-$($a[2]).json" '{"comments":[]}'; exit 0 }
        # Parenthesized: Windows PowerShell emits a JSON array as one object, which Where-Object would pass whole.
        $i = (Read-Text 'issues.json' '[]' | ConvertFrom-Json) | Where-Object { $_.number -eq [int]$a[2] }
        if ($i) { $i | ConvertTo-Json -Compress -Depth 10; exit 0 }
        [Console]::Error.WriteLine('issue not found'); exit 1
    }
    'pr list' {
        $head = Arg '--head'
        if (-not $head) { Read-Text 'others.json' '[]'; exit 0 }
        ConvertTo-Json -Compress -Depth 5 -InputObject @(Prs | Where-Object { $_.head -eq $head -and $_.state -eq 'OPEN' } | ForEach-Object { @{ number = $_.number; url = $_.url } })
        exit 0
    }
    'pr create' {
        $list = @(Prs)
        $n = 7 + $list.Count
        $url = "https://github.com/owner/repo/pull/$n"
        $list += [pscustomobject]@{ number = $n; url = $url; head = (Arg '--head'); base = (Arg '--base'); title = (Arg '--title'); state = 'OPEN' }
        Set-Content -LiteralPath (Join-Path $dir 'prs.json') -Value (ConvertTo-Json -Depth 5 -InputObject @($list))
        Copy-Item -LiteralPath (Arg '--body-file') -Destination (Join-Path $dir "pr-body-$n.md")
        $url
        exit 0
    }
    'pr comment' { Copy-Item -LiteralPath (Arg '--body-file') -Destination (Join-Path $dir "pr-comment-$($a[2]).md"); exit 0 }
    'pr view' { Read-Text "pr-$($a[2]).json" '{"state":"OPEN","mergeable":"MERGEABLE","statusCheckRollup":[],"comments":[],"reviews":[]}'; exit 0 }
    'run view' { 'FAIL greeting.test: expected hello'; exit 0 }
}
if ($a[0] -eq 'api') { Read-Text 'inline.json' '[]'; exit 0 }
exit 1
