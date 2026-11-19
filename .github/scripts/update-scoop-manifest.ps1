# Point the Scoop manifest at a release: set its version, url and hash, leaving the rest of the
# file (and its layout) as it is. Run by the release workflow; works in Windows PowerShell 5.1 too.
param(
    [Parameter(Mandatory)] [string] $Manifest,
    [Parameter(Mandatory)] [string] $Version,
    [Parameter(Mandatory)] [string] $Url,
    [Parameter(Mandatory)] [string] $Hash
)
$ErrorActionPreference = 'Stop'

$text = [IO.File]::ReadAllText((Resolve-Path $Manifest))
foreach ($field in @{ version = $Version; url = $Url; hash = $Hash }.GetEnumerator()) {
    $pattern = '("' + $field.Key + '"\s*:\s*)"[^"]*"'
    if ([regex]::Matches($text, $pattern).Count -ne 1) { throw "$Manifest should have one ""$($field.Key)"" field." }
    $text = [regex]::Replace($text, $pattern, { param($m) $m.Groups[1].Value + '"' + $field.Value + '"' })
}
# Check it's still a manifest, with the new values.
$parsed = $text | ConvertFrom-Json
if ($parsed.version -ne $Version -or $parsed.url -ne $Url -or $parsed.hash -ne $Hash) { throw "$Manifest wasn't updated as asked." }
[IO.File]::WriteAllText((Resolve-Path $Manifest), $text, (New-Object Text.UTF8Encoding $false))
