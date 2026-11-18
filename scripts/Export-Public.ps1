<#
.SYNOPSIS
  Copies the repository's tracked files, minus the private ones, into the public repository's clone, and refuses to
  finish if anything personal is left in them.
.DESCRIPTION
  The private repository keeps the development history and the working notes (AGENTS.md, STATUS, handoffs, research).
  The public repository gets a reviewed snapshot of everything else. Run from the private repository:

    .\scripts\Export-Public.ps1 -Destination C:\path\to\public-clone

  The destination must be a git clone of the public repository. Every exported file is copied over; files that the
  public repository tracks but the snapshot no longer has are removed with `git rm` (so they stay in history and can be
  restored). Nothing is committed or pushed: review `git status` there first.

  The scan looks for: this PC's name, this account's profile path, e-mail addresses other than example.com and GitHub
  no-reply addresses, GitHub tokens and private keys, and every line of the private patterns file (-Patterns, one regular
  expression per line, # for comments). That file is excluded from the snapshot, so it can name what must never be published.
#>
param(
    [Parameter(Mandatory)][string]$Destination,
    [string]$Patterns = (Join-Path $PSScriptRoot '..\docs\dev\public-scan-patterns.txt'),
    [switch]$ScanOnly
)
$ErrorActionPreference = 'Stop'
$root = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path

# Working notes that stay in the private repository.
$private = @('AGENTS.md', 'CLAUDE.md', 'docs/dev/STATUS.md', 'docs/dev/HANDOFF-desktop-widget.md', 'docs/dev/public-scan-patterns.txt')
$privateFolders = @('tools/research/')

$files = @(git -C $root ls-files) | Where-Object { $f = $_; $f -and $private -notcontains $f -and -not ($privateFolders | Where-Object { $f.StartsWith($_) }) }
if ($files.Count -lt 30) { throw "Only $($files.Count) tracked files found; is $root the repository?" }

# ---------- scan ----------
$rules = [System.Collections.Generic.List[object]]::new()
$rules.Add(@{ Name = 'this PC''s name'; Rx = '\b' + [regex]::Escape($env:COMPUTERNAME) + '\b' })
$rules.Add(@{ Name = 'this account''s profile'; Rx = [regex]::Escape($env:USERPROFILE) + '(\\|\b)' })
$rules.Add(@{ Name = 'GitHub token'; Rx = '\b(ghp|gho|ghu|ghs|ghr)_[A-Za-z0-9]{30,}|github_pat_[A-Za-z0-9_]{30,}' })
$rules.Add(@{ Name = 'private key'; Rx = '-----BEGIN [A-Z ]*PRIVATE KEY-----' })
$rules.Add(@{ Name = 'e-mail address'; Rx = '\b[A-Za-z0-9._%+-]+@(?!example\.(com|org)\b|users\.noreply\.github\.com\b|anthropic\.com\b)[A-Za-z0-9.-]+\.[A-Za-z]{2,}\b' })
if (Test-Path -LiteralPath $Patterns) {
    foreach ($line in Get-Content -LiteralPath $Patterns) {
        $t = $line.Trim()
        if ($t -and -not $t.StartsWith('#')) { $rules.Add(@{ Name = 'private pattern'; Rx = $t }) }
    }
} else { Write-Warning "No private patterns file at $Patterns; only the built-in checks run." }

$text = '\.(cs|xaml|ps1|psm1|md|txt|json|yml|yaml|xml|props|targets|csproj|slnx|sln|manifest|html|css|js|config|gitignore|editorconfig)$'
$hits = foreach ($f in $files) {
    if ($f -notmatch $text -and $f -notmatch '^[^.]+$') { continue }
    $path = Join-Path $root $f
    $n = 0
    foreach ($line in [IO.File]::ReadLines($path)) {
        $n++
        foreach ($r in $rules) {
            if ($line -match $r.Rx) { [pscustomobject]@{ File = $f; Line = $n; Rule = $r.Name } }
        }
    }
}
if ($hits) {
    # Only where and which rule: the matched text itself is what must not be printed into logs.
    $hits | Format-Table -AutoSize | Out-String | Write-Host
    throw "$(@($hits).Count) personal marker(s) found; fix them in the private repository and export again."
}
Write-Host "Scan clean: $($files.Count) files, $($rules.Count) rules."
if ($ScanOnly) { return }

# ---------- copy ----------
$dest = (Resolve-Path $Destination).Path
if ($dest.TrimEnd('\') -ieq $root.TrimEnd('\')) { throw 'The destination is this repository.' }
if (-not (Test-Path (Join-Path $dest '.git'))) { throw "$dest isn't a git clone of the public repository." }
foreach ($f in $files) {
    $target = Join-Path $dest $f
    New-Item -ItemType Directory -Force -Path (Split-Path $target) | Out-Null
    Copy-Item -LiteralPath (Join-Path $root $f) -Destination $target -Force
}
$gone = @(git -C $dest ls-files) | Where-Object { $_ -and $files -notcontains $_ }
foreach ($g in $gone) { git -C $dest rm --quiet -- $g }
Write-Host "Exported $($files.Count) files to $dest ($(@($gone).Count) removed with git rm; left out: $($private -join ', '), $($privateFolders -join ', '))."
