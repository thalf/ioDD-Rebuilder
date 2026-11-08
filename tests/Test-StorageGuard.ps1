param([string]$ScriptPath = "$PSScriptRoot/../docs/images/format-script-for-parser.ps1")
# All storage cmdlets below are mocks. This test never accesses or formats disks.
$ErrorActionPreference = 'Stop'
function Get-Partition {
    param($DriveLetter)
    $number = switch ($DriveLetter) { F { 2 } D { 3 } default { 0 } }
    if ($global:ioddTestScenario -eq 'source-disk' -and $DriveLetter -eq 'D') { $number = 2 }
    if ($global:ioddTestScenario -eq 'windows-disk' -and $DriveLetter -eq 'C') { $number = 2 }
    [pscustomobject]@{ DiskNumber = $number }
}
function Get-Disk {
    param($Number)
    if ($global:ioddTestScenario -eq 'query-failure') { throw 'Storage unavailable' }
    [pscustomobject]@{
        Number = $Number
        UniqueId = $(if ($global:ioddTestScenario -eq 'changed-disk') { 'different' } else { "disk'id" })
        IsBoot = $global:ioddTestScenario -eq 'boot'
        IsSystem = $global:ioddTestScenario -eq 'system'
        IsReadOnly = $global:ioddTestScenario -eq 'read-only'
        IsOffline = $global:ioddTestScenario -eq 'offline'
        BusType = $(if ($global:ioddTestScenario -eq 'internal') { 'SATA' } else { 'USB' })
    }
}
function Get-Volume {
    param($DriveLetter)
    [pscustomobject]@{
        UniqueId = $(if ($global:ioddTestScenario -eq 'changed-volume') { 'different' } else { "volume'id" })
        Size = $(if ($global:ioddTestScenario -eq 'changed-size') { 2048 } else { 1024 })
        Path = $(if ($global:ioddTestScenario -eq 'changed-path') {
            '\\?\Volume{22222222-2222-2222-2222-222222222222}\'
        } else { '\\?\Volume{11111111-1111-1111-1111-111111111111}\' })
        DriveType = 'Removable'
        FileSystem = 'exFAT'
        FileSystemLabel = 'IODD'
    }
}
function Format-Volume {
    param([Parameter(ValueFromPipeline)]$InputObject, $FileSystem, $NewFileSystemLabel, [switch]$Force, [switch]$Confirm)
    process {
        $global:ioddTestFormatted = $true
        if ($global:ioddTestScenario -eq 'format-failure') { throw 'Format failed' }
    }
}
$oldSystemDrive = $env:SystemDrive
try {
    $env:SystemDrive = 'C:'
    foreach ($global:ioddTestScenario in @('boot','system','windows-disk','source-disk','read-only','offline','internal','changed-disk','changed-volume','changed-size','changed-path','query-failure','format-failure','valid')) {
        $global:ioddTestFormatted = $false
        $failed = $false
        try { & $ScriptPath } catch { $failed = $true }
        if ($global:ioddTestScenario -eq 'valid') {
            if ($failed -or !$global:ioddTestFormatted) { throw 'Valid target rejected' }
        } elseif ($global:ioddTestScenario -eq 'format-failure') {
            if (!$failed) { throw 'Format error not propagated' }
        } elseif (!$failed -or $global:ioddTestFormatted) { throw "Unsafe target accepted: $global:ioddTestScenario" }
    }
    Write-Host 'PASS: 14 mocked storage scenarios; no real storage cmdlets executed.'
} finally { $env:SystemDrive = $oldSystemDrive }
