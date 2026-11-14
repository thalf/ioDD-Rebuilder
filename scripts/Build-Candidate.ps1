# Build and package a review candidate. All tests use temporary files or mocked storage.
[CmdletBinding()]
param()
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$root = Split-Path $PSScriptRoot -Parent
Push-Location $root
try {
    function Invoke-DotNet {
        & dotnet @args
        if ($LASTEXITCODE -ne 0) { throw "dotnet $($args -join ' ') failed ($LASTEXITCODE)." }
    }
    Invoke-DotNet build ioDDRebuilder/ioDDRebuilder.csproj -c Debug
    Invoke-DotNet build ioDDRebuilder/ioDDRebuilder.csproj -c Release
    Invoke-DotNet build ioDDRebuilder.Formatter/ioDDRebuilder.Formatter.csproj -c Release
    Invoke-DotNet run --project tests/SafetyTests.csproj -c Release -- docs/images
    $parseErrors = $null
    [System.Management.Automation.Language.Parser]::ParseFile(
        "$root/docs/images/format-script-for-parser.ps1", [ref]$null, [ref]$parseErrors) | Out-Null
    if ($parseErrors.Count) { throw ($parseErrors | Out-String) }
    & "$root/tests/Test-StorageGuard.ps1"

    $artifacts = Join-Path $root 'artifacts'
    $stage = Join-Path $artifacts ('stage-' + [guid]::NewGuid().ToString('N'))
    New-Item -ItemType Directory -Path $stage -Force | Out-Null
    Invoke-DotNet publish ioDDRebuilder/ioDDRebuilder.csproj -c Release -r win-x64 --self-contained false -o $stage
    Invoke-DotNet publish ioDDRebuilder.Formatter/ioDDRebuilder.Formatter.csproj -c Release -r win-x64 --self-contained false -o "$stage/formatter"
    $executables = @(
        "$root/ioDDRebuilder/bin/Debug/net8.0-windows/ioDDRebuilder.exe",
        "$root/ioDDRebuilder/bin/Release/net8.0-windows/ioDDRebuilder.exe",
        "$root/ioDDRebuilder.Formatter/bin/Release/net8.0-windows/IODDRebuilder.Formatter.exe",
        "$stage/ioDDRebuilder.exe",
        "$stage/formatter/IODDRebuilder.Formatter.exe")
    & "$root/tests/Test-ExecutableIcon.ps1" -Executable $executables
    Copy-Item "$root/LICENSE", "$root/README.md" -Destination $stage
    Copy-Item "$root/docs/BUILD.md" -Destination "$stage/BUILD-AND-VALIDATION.md"
    $sha = & git rev-parse HEAD
    if ($LASTEXITCODE -ne 0) { throw 'Cannot identify candidate source commit.' }
    @{ version = '1.1.0-candidate'; sourceCommit = $sha; runtime = 'win-x64'; selfContained = $false } |
        ConvertTo-Json | Set-Content "$stage/BUILDINFO.json" -Encoding utf8
    $manifest = @(Get-ChildItem $stage -Recurse -File | ForEach-Object {
        [pscustomobject]@{ path = [IO.Path]::GetRelativePath($stage, $_.FullName); sha256 = (Get-FileHash $_.FullName -Algorithm SHA256).Hash }
    })
    ConvertTo-Json -InputObject $manifest | Set-Content "$stage/MANIFEST.json" -Encoding utf8
    $zip = Join-Path $artifacts 'ioDD-Rebuilder_v1.1.0-candidate_win-x64.zip'
    Compress-Archive -Path "$stage/*" -DestinationPath $zip -Force
    $extracted = Join-Path $artifacts ('verify-' + [guid]::NewGuid().ToString('N'))
    Expand-Archive -LiteralPath $zip -DestinationPath $extracted
    foreach ($entry in $manifest) {
        if ((Get-FileHash (Join-Path $extracted $entry.path) -Algorithm SHA256).Hash -ne $entry.sha256) {
            throw "Packaged file differs: $($entry.path)"
        }
    }
    if (Get-ChildItem $extracted -Recurse -Filter '*.ico') { throw 'Candidate must not depend on a loose ICO.' }
    & "$root/tests/Test-ExecutableIcon.ps1" -Executable @("$extracted/ioDDRebuilder.exe", "$extracted/formatter/IODDRebuilder.Formatter.exe")
    $hash = (Get-FileHash $zip -Algorithm SHA256).Hash.ToLowerInvariant()
    "$hash  $([IO.Path]::GetFileName($zip))" | Set-Content "$zip.sha256" -Encoding ascii
    Write-Host "PASS: candidate archive round-trip, manifest, and packaged executable icons verified. Source: $sha"
    Write-Host "Candidate: $zip"
} finally { Pop-Location }
