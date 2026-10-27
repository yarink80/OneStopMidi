<#
.SYNOPSIS
    Builds the OneStopMidi installer and portable package.

.DESCRIPTION
    1. dotnet publish src\OneStopMidi.App (Release, win-x64, self-contained, single folder, no PDBs)
    2. vpk pack (per-user install to %LOCALAPPDATA%\OneStopMidi, no admin, Desktop + Start menu shortcuts)
    3. dist\<version>\: OneStopMidi-Setup.exe, OneStopMidi-Portable.zip, SHA256SUMS.txt
       (Velopack's own files - *.nupkg, releases.win.json, RELEASES - stay in artifacts\package\<version>\vpk for
       `vpk upload github`, which is what installed copies use for "Check for updates").

.EXAMPLE
    ./tools/package.ps1 -Version 0.1.0
    ./tools/package.ps1 -Version 0.1.1 -DownloadPrevious -RepoUrl https://github.com/yarink80/OneStopMidi -Token $env:GITHUB_TOKEN
#>
param(
    [Parameter(Mandatory)] [string] $Version,
    [string] $Channel = 'win',
    [switch] $DownloadPrevious,
    [string] $RepoUrl = 'https://github.com/yarink80/OneStopMidi',
    [string] $Token,
    [switch] $Prerelease,
    [string] $VpkVersion = '1.2.161'
)
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$app = Join-Path $root 'src\OneStopMidi.App\OneStopMidi.App.csproj'
$work = Join-Path $root "artifacts\package\$Version"
$publish = Join-Path $work 'publish'
$vpkOut = Join-Path $work 'vpk'
$dist = Join-Path $root "dist\$Version"
$icon = Join-Path $root 'src\OneStopMidi.App\Assets\OneStopMidi.ico'
$notes = Join-Path $root "docs\RELEASE-NOTES-$Version.md"

function Invoke-Step([string] $Name, [scriptblock] $Body) {
    Write-Host "==> $Name" -ForegroundColor Cyan
    & $Body
    if ($LASTEXITCODE -ne 0) { throw "$Name failed (exit $LASTEXITCODE)" }
}

# ---------------------------------------------------------------- vpk tool (pinned)
$installed = (& dotnet tool list -g) | Select-String -Pattern '^\s*vpk\s+(\S+)' | ForEach-Object { $_.Matches[0].Groups[1].Value }
if ($installed -ne $VpkVersion) {
    Invoke-Step "dotnet tool update -g vpk $VpkVersion" { dotnet tool update -g vpk --version $VpkVersion }
}

# ---------------------------------------------------------------- 1. clean publish
Remove-Item $work, $dist -Recurse -Force -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Force -Path $publish, $vpkOut, $dist | Out-Null
Invoke-Step "dotnet publish (Release, self-contained, $Version)" {
    dotnet publish $app -c Release -r win-x64 --self-contained true -o $publish `
        -p:Version=$Version -p:DebugType=none -p:PublishReadyToRun=true
}

# ---------------------------------------------------------------- 2. previous release (delta) + vpk pack
if ($DownloadPrevious) {
    $dl = @('download', 'github', '--repoUrl', $RepoUrl, '--channel', $Channel, '--outputDir', $vpkOut)
    if ($Token) { $dl += @('--token', $Token) }
    if ($Prerelease) { $dl += '--pre' }
    Write-Host "==> vpk download github (previous release for delta packages)" -ForegroundColor Cyan
    & vpk @dl
    if ($LASTEXITCODE -ne 0) { Write-Warning "vpk download github failed (exit $LASTEXITCODE): no previous release, so no delta package"; $global:LASTEXITCODE = 0 }
}
$vpkArgs = @(
    'pack', '--yes',
    '--packId', 'OneStopMidi', '--packVersion', $Version, '--packDir', $publish, '--mainExe', 'OneStopMidi.exe',
    '--packTitle', 'OneStopMidi', '--packAuthors', 'OneStopMidi contributors',
    '--icon', $icon, '--channel', $Channel, '--runtime', 'win-x64', '--outputDir', $vpkOut,
    '--shortcuts', 'Desktop,StartMenuRoot'
)
if (Test-Path $notes) { $vpkArgs += @('--releaseNotes', $notes) }
Invoke-Step "vpk pack OneStopMidi $Version ($Channel)" { & vpk @vpkArgs }

# ---------------------------------------------------------------- 3. dist\<version> with friendly names
Copy-Item (Join-Path $vpkOut "OneStopMidi-$Channel-Setup.exe") (Join-Path $dist 'OneStopMidi-Setup.exe')
Copy-Item (Join-Path $vpkOut "OneStopMidi-$Channel-Portable.zip") (Join-Path $dist 'OneStopMidi-Portable.zip')
$sums = Get-ChildItem $dist -File | Where-Object Name -ne 'SHA256SUMS.txt' | Sort-Object Name | ForEach-Object {
    '{0}  {1}' -f (Get-FileHash $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant(), $_.Name
}
[IO.File]::WriteAllLines((Join-Path $dist 'SHA256SUMS.txt'), [string[]]$sums)

Write-Host ''
Get-ChildItem $dist -File | ForEach-Object { Write-Host ("{0,-28} {1,8:N1} MB" -f $_.Name, ($_.Length / 1MB)) }
Write-Host "vpk output: $vpkOut (upload with: vpk upload github --outputDir <this>)"
