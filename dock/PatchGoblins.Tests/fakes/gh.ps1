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

# The test writes these files as UTF-8 without a BOM, which Windows PowerShell 5.1 reads as ANSI unless told, and may be
# writing one right now (the file is then busy): retry rather than fail the call.
function Read-Text($name, $default) {
    $p = Join-Path $dir $name
    for ($i = 0; ; $i++) {
        try { if (Test-Path -LiteralPath $p) { return Get-Content -Raw -Encoding UTF8 -LiteralPath $p } else { return $default } }
        catch [System.IO.IOException] { if ($i -ge 40) { throw }; Start-Sleep -Milliseconds 25 }
    }
}
# Whole file at once: a reader (this fake, another call) never sees half a list. Windows PowerShell's Set-Content would write ANSI.
function Write-Text($name, $text) {
    $p = Join-Path $dir $name
    $tmp = "$p.$PID.tmp"
    [IO.File]::WriteAllText($tmp, $text, (New-Object System.Text.UTF8Encoding($false)))
    for ($i = 0; ; $i++) {
        try { if (Test-Path -LiteralPath $p) { [IO.File]::Replace($tmp, $p, [NullString]::Value) } else { [IO.File]::Move($tmp, $p) }; return }
        catch [System.IO.IOException] { if ($i -ge 40) { throw }; Start-Sleep -Milliseconds 25 }
    }
}
function Arg($name) { $i = [array]::IndexOf($a, $name); if ($i -ge 0) { $a[$i + 1] } }
# Not piped: Windows PowerShell 5.1 emits a piped JSON array as one object, so `@(... | ConvertFrom-Json)` of '[]' counted 1 and the first PR was #8.
function Prs() { $j = ConvertFrom-Json (Read-Text 'prs.json' '[]'); @($j) }

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
        # Two creates at once must not count the same list (and take one number): the lock holds from reading to writing.
        $lock = $null
        for ($i = 0; $i -lt 200 -and -not $lock; $i++) {
            try { $lock = [IO.File]::Open((Join-Path $dir 'prs.lock'), 'OpenOrCreate', 'ReadWrite', 'None') } catch [System.IO.IOException] { Start-Sleep -Milliseconds 25 }
        }
        try {
            $list = @(Prs)
            $n = 7 + $list.Count
            $url = "https://github.com/owner/repo/pull/$n"
            $list += [pscustomobject]@{ number = $n; url = $url; head = (Arg '--head'); base = (Arg '--base'); title = (Arg '--title'); state = 'OPEN' }
            Write-Text 'prs.json' (ConvertTo-Json -Depth 5 -InputObject @($list))
        } finally { if ($lock) { $lock.Dispose() } }
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
