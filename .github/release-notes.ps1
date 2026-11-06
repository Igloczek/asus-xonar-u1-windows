# Prints the release description for one version, taken from CHANGELOG.md.
# Fails when the version has no changelog entry, so a release can't go out without one.
param([Parameter(Mandatory)] [string] $Version, [string] $Changelog = 'CHANGELOG.md')

$lines = Get-Content -LiteralPath $Changelog -Encoding utf8
$start = -1
for ($i = 0; $i -lt $lines.Count; $i++) {
  if ($lines[$i] -match ('^## ' + [regex]::Escape($Version) + '( |$)')) { $start = $i + 1; break }
}
if ($start -lt 0) { throw "CHANGELOG.md has no entry for $Version" }

$entry = for ($i = $start; $i -lt $lines.Count -and $lines[$i] -notmatch '^## '; $i++) { $lines[$i] }
$entry = ($entry -join "`n").Trim()
if (-not $entry) { throw "The CHANGELOG.md entry for $Version is empty" }

$install = @'
**To install**, download **XonarU1-Setup.exe** below and run it. Already have version 1.0.4 or newer? Click **Check for updates** in the panel instead.
'@
"$entry`n`n$install"
