# Point the Scoop manifest at a release: its version, the exe's download URL and its SHA-256.
# The release workflow runs it; it works the same by hand. The three values are replaced in place,
# so the file keeps its layout.
param(
    [Parameter(Mandatory)] [string] $Manifest,
    [Parameter(Mandatory)] [string] $Version,
    [Parameter(Mandatory)] [string] $Hash
)
$ErrorActionPreference = 'Stop'

$path = (Resolve-Path $Manifest).Path
$text = [System.IO.File]::ReadAllText($path)
$replacements = @(
    @('(?m)^(    "version": ")[^"]*(")', "`${1}$Version`${2}"),
    @('(/releases/download/v)\d+\.\d+\.\d+(/GameBackdrop\.exe")', "`${1}$Version`${2}"),
    @('("hash": ")[^"]*(")', "`${1}$Hash`${2}")
)
foreach ($r in $replacements) {
    $regex = [regex]$r[0]
    if ($regex.Matches($text).Count -ne 1) { throw "Expected exactly one match for $($r[0]) in $Manifest." }
    $text = $regex.Replace($text, $r[1])
}
# UTF-8 without a BOM, as Scoop's own manifests are.
[System.IO.File]::WriteAllText($path, $text, [System.Text.UTF8Encoding]::new($false))
