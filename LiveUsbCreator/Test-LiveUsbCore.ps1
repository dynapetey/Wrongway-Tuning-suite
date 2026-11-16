# Simulate Windows Storage commands. This test never opens an actual disk.
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'LiveUsbCore.ps1')

$script:disk = [pscustomobject]@{
    Number = 3; NumberOfPartitions = 1; BusType = 'USB'; IsSystem = $false; IsBoot = $false
    IsOffline = $false; IsReadOnly = $false; Path = 'usb-device-path'; UniqueId = 'stable-id'; SerialNumber = '123'
    FriendlyName = 'Test USB'; Size = [uint64]8000000000; PartitionStyle = 'GPT'
}
$script:operations = [Collections.Generic.List[string]]::new()
$script:corrupt = $false

function Get-Disk { param($Number, $ErrorAction); $script:disk }
function Get-Partition {
    param($DiskNumber, $DriveLetter, $ErrorAction)
    [pscustomobject]@{ DiskNumber = 3; PartitionNumber = 1; DriveLetter = 'F'; AccessPaths = @('F:\') }
}
function Get-VerifiedIso { param($Iso, $Cache); [pscustomobject]@{ IsoPath = '/fake.iso' } }
function Get-DiskImage { param($ImagePath, $ErrorAction); [pscustomobject]@{ Attached = $true } }
function Get-Volume {
    param([Parameter(ValueFromPipeline)]$InputObject)
    process { [pscustomobject]@{ DriveLetter = 'E' } }
}
function Get-CopyPlan {
    param($Payload, $IsoRoot)
    if ($script:corrupt) { throw 'Test corrupt media' }
    $script:operations.Add('verify')
    [pscustomobject]@{ Name = 'payload'; Bytes = 2048 }
}
function Clear-Disk {
    param($InputObject, [switch]$RemoveData, [switch]$RemoveOEM, [switch]$Confirm, $ErrorAction)
    $script:operations.Add('erase')
}
function Initialize-Disk {
    param($InputObject, $PartitionStyle, [switch]$Confirm, $ErrorAction)
    $script:operations.Add('initialize')
}
function New-Partition {
    param($DiskNumber, $Size, $Alignment, $MbrType, [switch]$AssignDriveLetter, $ErrorAction)
    if ($MbrType -ne 'FAT32' -or $Size -gt 29GB) { throw 'Unexpected partition layout' }
    $script:operations.Add('partition')
    [pscustomobject]@{ PartitionNumber = 1; DriveLetter = 'F' }
}
function Format-Volume {
    param($Partition, $FileSystem, $AllocationUnitSize, $NewFileSystemLabel, [switch]$Force,
        [switch]$Confirm, $ErrorAction)
    $script:operations.Add('format')
    [pscustomobject]@{ FileSystem = $FileSystem; SizeRemaining = 7GB }
}
function Copy-UsbFile { param($File, $Root); $script:operations.Add('write') }

function Assert-FailsBeforeErase {
    param($Target, $Message)
    $script:operations.Clear()
    $failure = $null
    try { Install-WrongWayUsb $Target '/payload' '/fake.iso' '/cache' | Out-Null }
    catch { $failure = $_.Exception.Message }
    if ($failure -notmatch $Message -or $script:operations.Contains('erase')) {
        throw "Expected a pre-erase failure matching '$Message'; got '$failure'."
    }
}

$target = Get-UsbTargets
if ($target.Caption -notmatch 'Test USB.+8.+F:') { throw 'USB selection details were lost.' }
$disk.IsSystem = $true
Assert-FailsBeforeErase $target 'no longer eligible'
$disk.IsSystem = $false
$disk.UniqueId = 'different-id'
Assert-FailsBeforeErase $target 'changed'
$disk.UniqueId = 'stable-id'
$script:corrupt = $true
Assert-FailsBeforeErase $target 'USB was not erased'
$script:corrupt = $false
Install-WrongWayUsb $target '/payload' '/fake.iso' '/cache' | Out-Null
if (($script:operations -join ',') -ne 'verify,erase,initialize,partition,format,write') {
    throw ('Unexpected operation order: ' + ($script:operations -join ','))
}
'Verified system disk rejection, device replacement rejection, corrupt media rejection, and operation order.'
