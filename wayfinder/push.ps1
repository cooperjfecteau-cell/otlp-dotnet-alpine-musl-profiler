# Creates the wayfinder map and its child tickets as GitHub issues.
#
# Parses wayfinder/tickets.md, creates one issue per section, links each as a
# sub-issue of the map, and wires blocked-by dependency edges. Idempotency is NOT
# attempted -- running twice creates duplicates. Run once.
#
# This file is deliberately ASCII-only: PowerShell 5.1 reads BOM-less UTF-8 source
# as ANSI, so a stray em-dash here is a parse error. Files it *reads* are opened as
# UTF-8 explicitly, and everything it writes is UTF-8 without BOM, because gh would
# otherwise carry a BOM into the issue body.

$ErrorActionPreference = 'Stop'
$env:Path = [System.Environment]::GetEnvironmentVariable("Path","Machine") + ";" +
            [System.Environment]::GetEnvironmentVariable("Path","User")

function Write-Utf8NoBom([string]$Path, [string]$Text) {
  [System.IO.File]::WriteAllText($Path, $Text, (New-Object System.Text.UTF8Encoding($false)))
}

$root = Split-Path -Parent $PSScriptRoot
Set-Location $root

$repo = (gh repo view --json nameWithOwner --jq .nameWithOwner)
Write-Host "repo: $repo"

# --- labels -----------------------------------------------------------------
$labels = @{
  'wayfinder:map'       = '5319e7'
  'wayfinder:research'  = '0e8a16'
  'wayfinder:prototype' = 'fbca04'
  'wayfinder:grilling'  = '1d76db'
  'wayfinder:task'      = 'd93f0b'
}
foreach ($l in $labels.Keys) {
  try { gh label create $l --color $labels[$l] --force | Out-Null; Write-Host "label $l" }
  catch { Write-Host "label $l (exists)" }
}

# --- map --------------------------------------------------------------------
$mapRaw   = Get-Content (Join-Path $PSScriptRoot 'map.md') -Raw -Encoding UTF8
$mapTitle = ([regex]::Match($mapRaw, '(?m)^#\s+(.+)$')).Groups[1].Value.Trim()
$mapBody  = ($mapRaw -replace '(?m)^#\s+.+\r?\n', '').Trim()

$tmp = [System.IO.Path]::GetTempFileName()
Write-Utf8NoBom $tmp $mapBody
$mapUrl = gh issue create --title $mapTitle --body-file $tmp --label 'wayfinder:map'
Remove-Item $tmp
$mapNum = [int]($mapUrl -split '/')[-1]
Write-Host "map: #$mapNum  $mapUrl"

# --- parse tickets ----------------------------------------------------------
$raw   = Get-Content (Join-Path $PSScriptRoot 'tickets.md') -Raw -Encoding UTF8
$parts = [regex]::Split($raw, '(?m)^##\s+(?=\S+\s+\|)')
$tickets = @()

foreach ($p in $parts) {
  if ($p -notmatch '^\S+\s+\|') { continue }
  $lines  = $p -split "\r?\n"
  $header = $lines[0]
  $id     = ($header -split '\|')[0].Trim()
  $title  = ($header -split '\|', 2)[1].Trim()

  $sep = [array]::IndexOf($lines, '---')
  $meta = $lines[1..($sep-1)]
  $body = ($lines[($sep+1)..($lines.Length-1)] -join "`n").Trim()

  $lbl = ''; $blocked = @(); $state = 'open'
  foreach ($m in $meta) {
    if ($m -match '^labels:\s*(.*)$')     { $lbl = $Matches[1].Trim() }
    if ($m -match '^blocked-by:\s*(.*)$') {
      $v = $Matches[1].Trim()
      if ($v) { $blocked = ($v -split ',') | ForEach-Object { $_.Trim() } }
    }
    if ($m -match '^state:\s*(.*)$')      { $state = $Matches[1].Trim() }
  }

  $tickets += [pscustomobject]@{
    Id = $id; Title = $title; Labels = $lbl; Blocked = $blocked; State = $state; Body = $body
  }
}
Write-Host "parsed $($tickets.Count) tickets"

# --- create issues ----------------------------------------------------------
$reg = @{}
foreach ($t in $tickets) {
  $tmp = [System.IO.Path]::GetTempFileName()
  Write-Utf8NoBom $tmp ("Part of #$mapNum`n`n" + $t.Body)
  $url = gh issue create --title $t.Title --body-file $tmp --label $t.Labels
  Remove-Item $tmp
  $num  = [int]($url -split '/')[-1]
  $dbid = gh api "repos/$repo/issues/$num" --jq .id
  $reg[$t.Id] = [pscustomobject]@{ Number = $num; DbId = $dbid; Url = $url }
  Write-Host ("  {0,-4} -> #{1}  {2}" -f $t.Id, $num, $t.Title)
}

# --- link as sub-issues of the map -----------------------------------------
$subOk = 0; $subFail = 0
foreach ($t in $tickets) {
  $child = $reg[$t.Id]
  try {
    gh api --method POST "repos/$repo/issues/$mapNum/sub_issues" -F sub_issue_id=$($child.DbId) 2>$null | Out-Null
    $subOk++
  } catch { $subFail++ }
}
Write-Host "sub-issues linked: $subOk ok, $subFail failed"

# --- blocking edges ---------------------------------------------------------
$depOk = 0; $depFallback = 0
foreach ($t in $tickets) {
  if (-not $t.Blocked) { continue }
  $child = $reg[$t.Id]
  foreach ($b in $t.Blocked) {
    if (-not $reg.ContainsKey($b)) { Write-Host "  !! unknown blocker $b for $($t.Id)"; continue }
    $blk = $reg[$b]
    $ok = $false
    try {
      gh api --method POST "repos/$repo/issues/$($child.Number)/dependencies/blocked_by" -F issue_id=$($blk.DbId) 2>$null | Out-Null
      $ok = $true; $depOk++
    } catch { $ok = $false }
    if (-not $ok) {
      # Native dependencies unavailable -- fall back to a body line so the gate is
      # still legible to a human and to the frontier query.
      $cur = gh issue view $child.Number --json body --jq .body
      if ($cur -notmatch '(?m)^Blocked by:') {
        $tmp = [System.IO.Path]::GetTempFileName()
        Write-Utf8NoBom $tmp ("Blocked by: #$($blk.Number)`n`n" + $cur)
        gh issue edit $child.Number --body-file $tmp | Out-Null
        Remove-Item $tmp
      } else {
        $tmp = [System.IO.Path]::GetTempFileName()
        Write-Utf8NoBom $tmp ($cur -replace '(?m)^(Blocked by:.*)$', "`$1, #$($blk.Number)")
        gh issue edit $child.Number --body-file $tmp | Out-Null
        Remove-Item $tmp
      }
      $depFallback++
    }
  }
}
Write-Host "dependencies: $depOk native, $depFallback body-annotated"

# --- close pre-resolved tickets --------------------------------------------
foreach ($t in $tickets) {
  if ($t.State -ne 'closed') { continue }
  $c = $reg[$t.Id]
  gh issue close $c.Number --comment "Settled during charting, before the map was laid down. Body holds the full record." | Out-Null
  Write-Host "closed #$($c.Number)"
}

# --- point the map's Decisions-so-far at the real charter issue -------------
if ($reg.ContainsKey('C1')) {
  $charter = $reg['C1']
  $newBody = $mapBody -replace '(\[Charter[^\]]*\])\(#\)', "`$1($($charter.Url))"
  $tmp = [System.IO.Path]::GetTempFileName()
  Write-Utf8NoBom $tmp $newBody
  gh issue edit $mapNum --body-file $tmp | Out-Null
  Remove-Item $tmp
  Write-Host "map Decisions-so-far -> #$($charter.Number)"
}

Write-Host ""
Write-Host "MAP: $mapUrl"
