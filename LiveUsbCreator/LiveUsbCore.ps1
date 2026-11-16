Set-StrictMode -Version Latest
$script:MinimumUsbBytes = [uint64]7500000000
$script:BaseIsoName = 'linuxmint-21.1-xfce-64bit.iso'
$script:BaseIsoHash = '6fea221b5b0272d55de57f3d31498cdf76682f414e60d28131dc428e719efa8b'
$script:BaseIsoUrls = @(
    'https://mirror.fcix.net/linuxmint-images/stable/21.1/linuxmint-21.1-xfce-64bit.iso',
    'https://pub.linuxmint.io/stable/21.1/linuxmint-21.1-xfce-64bit.iso'
)

function Test-UsbTarget {
    param($Disk)
    return $null -ne $Disk -and $Disk.BusType -eq 'USB' -and -not $Disk.IsBoot -and
        -not $Disk.IsSystem -and -not $Disk.IsOffline -and -not $Disk.IsReadOnly -and
        $Disk.Size -ge $script:MinimumUsbBytes -and -not [string]::IsNullOrWhiteSpace($Disk.Path)
}

function Get-UsbTargets {
    foreach ($disk in Get-Disk -ErrorAction Stop) {
        if (Test-UsbTarget $disk) {
            $partitions = @()
            if ($disk.NumberOfPartitions -gt 0) {
                $partitions = @(Get-Partition -DiskNumber $disk.Number -ErrorAction Stop)
            }
            $letters = @($partitions | Where-Object { $_.DriveLetter -match '^[A-Z]$' } |
                ForEach-Object { '{0}:' -f $_.DriveLetter })
            $suffix = if ($letters.Count) { '  ' + ($letters -join ', ') } else { '' }
            [pscustomobject]@{
                Number = $disk.Number
                Size = $disk.Size
                Path = $disk.Path
                UniqueId = $disk.UniqueId
                SerialNumber = $disk.SerialNumber
                Caption = 'Disk {0}  |  {1}  |  {2:N1} GB{3}' -f $disk.Number, $disk.FriendlyName,
                    ($disk.Size / 1000000000), $suffix
            }
        }
    }
}

function Get-ConfirmedDisk {
    param($Target)
    $disk = Get-Disk -Number $Target.Number -ErrorAction Stop
    if (-not (Test-UsbTarget $disk) -or $disk.Path -ne $Target.Path -or $disk.UniqueId -ne $Target.UniqueId -or
        $disk.SerialNumber -ne $Target.SerialNumber -or $disk.Size -ne $Target.Size) {
        throw 'The selected USB drive changed or is no longer eligible. Refresh and select the drive again.'
    }
    return $disk
}

function Assert-SourcesOffTarget {
    param($Disk, [string[]]$Paths)
    if ($Disk.NumberOfPartitions -eq 0) { return }
    $sourcePaths = @($Paths | Where-Object { $_ } | ForEach-Object { [IO.Path]::GetFullPath($_) })
    $mounts = Get-Partition -DiskNumber $Disk.Number -ErrorAction Stop | ForEach-Object { $_.AccessPaths }
    foreach ($mount in $mounts) {
        if ([string]::IsNullOrWhiteSpace($mount)) { continue }
        $root = $mount.TrimEnd('\') + '\'
        foreach ($path in $sourcePaths) {
            if ($path.StartsWith($root, [StringComparison]::OrdinalIgnoreCase) -or
                $path.TrimEnd('\').Equals($root.TrimEnd('\'), [StringComparison]::OrdinalIgnoreCase)) {
                throw 'Move the creator and your ISO off the USB drive before selecting it.'
            }
        }
    }
}

function Assert-FileHash {
    param([string]$Path, [string]$Hash)
    if (-not (Test-Path -LiteralPath $Path -PathType Leaf) -or
        (Get-FileHash -LiteralPath $Path -Algorithm SHA256 -ErrorAction Stop).Hash -ne $Hash) {
        throw "Missing file or SHA-256 checksum mismatch: $Path"
    }
}

function Get-VerifiedIso {
    param([string]$Iso, [string]$Cache)
    if ($Iso) {
        Write-Output 'Verifying the selected Linux Mint ISO...'
        $Iso = (Resolve-Path -LiteralPath $Iso -ErrorAction Stop).ProviderPath
        if ([IO.Path]::GetExtension($Iso) -ne '.iso') { throw 'Choose a Linux Mint .iso file.' }
        Assert-FileHash $Iso $script:BaseIsoHash
        return [pscustomobject]@{ IsoPath = $Iso }
    }

    $file = Join-Path $Cache $script:BaseIsoName
    if (Test-Path -LiteralPath $file -PathType Leaf) {
        Write-Output 'Verifying the cached Linux Mint ISO...'
        Assert-FileHash $file $script:BaseIsoHash
        return [pscustomobject]@{ IsoPath = $file }
    }

    $null = New-Item -ItemType Directory -Path $Cache -Force -ErrorAction Stop
    $temporary = $file + '.' + [guid]::NewGuid().ToString('N') + '.partial'
    $ProgressPreference = 'SilentlyContinue'
    [Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
    try {
        foreach ($url in $script:BaseIsoUrls) {
            Write-Output "Downloading the Linux Mint base from $(([uri]$url).Host) (about 2.7 GB)..."
            try {
                $null = Invoke-WebRequest -Uri $url -OutFile $temporary -UseBasicParsing -ErrorAction Stop
                Write-Output 'Verifying the downloaded ISO...'
                Assert-FileHash $temporary $script:BaseIsoHash
                Move-Item -LiteralPath $temporary -Destination $file -Force -ErrorAction Stop
                return [pscustomobject]@{ IsoPath = $file }
            } catch {
                Write-Output "Download failed: $($_.Exception.Message)"
                Remove-Item -LiteralPath $temporary -Force -ErrorAction SilentlyContinue
            }
        }
        throw 'Download failed on both mirrors. Choose a verified Linux Mint 21.1 Xfce 64-bit ISO under Browse.'
    } finally {
        Remove-Item -LiteralPath $temporary -Force -ErrorAction SilentlyContinue
    }
}

function Get-CopyPlan {
    param([string]$Payload, [string]$IsoRoot)
    $manifestPath = Join-Path $Payload 'manifest.json'
    if (-not (Test-Path -LiteralPath $manifestPath -PathType Leaf)) {
        throw 'The payload is missing. Extract the full creator ZIP into a folder before running it.'
    }
    $manifest = Get-Content -LiteralPath $manifestPath -Raw -ErrorAction Stop | ConvertFrom-Json -ErrorAction Stop
    if ($manifest.Schema -ne 1 -or $manifest.SuiteVersion -notmatch '^\d+\.\d+\.\d+$') {
        throw 'The creator package has an unsupported manifest.'
    }
    $required = @('casper/vmlinuz', 'casper/initrd.lz', 'casper/filesystem.squashfs',
        'casper/zz-wrongway.squashfs', 'casper/zzz-wrongway-update.squashfs',
        'EFI/boot/bootx64.efi', 'EFI/boot/grub.cfg', 'EFI/boot/grubx64.efi', 'boot/grub/grub.cfg', 'writable')
    $seen = @{}
    foreach ($entry in $manifest.Files) {
        $name = [string]$entry.Name
        if ($name -notmatch '^[A-Za-z0-9._-]+(/[A-Za-z0-9._-]+)*$' -or
            ($name -split '/') -contains '..' -or ($name -split '/') -contains '.' -or $seen.ContainsKey($name) -or
            $entry.Hash -notmatch '^[0-9a-f]{64}$' -or $entry.Bytes -lt 0 -or $entry.Bytes -gt 4294967295L) {
            throw "Invalid file in package manifest: $name"
        }
        $seen[$name] = $true
        $relative = $name.Replace('/', [IO.Path]::DirectorySeparatorChar)
        switch ($entry.Kind) {
            'base' { $source = Join-Path $IsoRoot $relative; $hash = $entry.Hash }
            'bundle' { $source = Join-Path (Join-Path $Payload 'files') $relative; $hash = $entry.Hash }
            'gzip' {
                if ($name -ne 'writable' -or $entry.StoredHash -notmatch '^[0-9a-f]{64}$') {
                    throw 'The persistence file entry is invalid.'
                }
                $source = Join-Path $Payload 'writable.gz'
                $hash = $entry.StoredHash
            }
            default { throw "Unsupported payload entry: $name" }
        }
        if (-not (Test-Path -LiteralPath $source -PathType Leaf)) {
            throw "Windows could not read the required file: $source"
        }
        if ($entry.Kind -ne 'gzip' -and (Get-Item -LiteralPath $source).Length -ne $entry.Bytes) {
            throw "Unexpected file size: $source"
        }
        Assert-FileHash $source $hash
        [pscustomobject]@{ Name = $relative; Source = $source; Hash = $entry.Hash; Bytes = $entry.Bytes
            Decompress = ($entry.Kind -eq 'gzip') }
    }
    foreach ($name in $required) {
        if (-not $seen.ContainsKey($name)) { throw "Creator package is missing $name" }
    }
}

function Copy-UsbFile {
    param($File, [string]$Root)
    $destination = Join-Path $Root $File.Name
    $null = [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($destination))
    $inputFile = [IO.File]::OpenRead($File.Source)
    try {
        $reader = $inputFile
        if ($File.Decompress) {
            $reader = [IO.Compression.GZipStream]::new($inputFile, [IO.Compression.CompressionMode]::Decompress, $true)
        }
        try {
            $output = [IO.File]::Open($destination, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write, [IO.FileShare]::None)
            try {
                $reader.CopyTo($output, 1MB)
                if ($output.Length -ne $File.Bytes) { throw "Copy length mismatch for $($File.Name)." }
                $output.Flush($true)
            } finally { $output.Dispose() }
        } finally {
            if ($File.Decompress) { $reader.Dispose() }
        }
    } finally { $inputFile.Dispose() }
    Assert-FileHash $destination $File.Hash
}

function Get-UsbPartitionSize {
    param([uint64]$Bytes)
    return [uint64][Math]::Min(29GB, [Math]::Floor(($Bytes - 2MB) / 1MB) * 1MB)
}

function Install-WrongWayUsb {
    param($Target, [string]$Payload, [string]$Iso, [string]$Cache)
    $disk = Get-ConfirmedDisk $Target
    Assert-SourcesOffTarget $disk @($Payload, $Iso, $Cache)
    $isoPath = $null
    $mountedByCreator = $false
    $erasing = $false
    try {
        Get-VerifiedIso $Iso $Cache | ForEach-Object {
            if ($_ -is [string]) { Write-Output $_ } else { $isoPath = $_.IsoPath }
        }
        $source = Get-DiskImage -ImagePath $isoPath -ErrorAction Stop
        if (-not $source.Attached) {
            $source = Mount-DiskImage -ImagePath $isoPath -StorageType ISO -Access ReadOnly -PassThru -ErrorAction Stop
            $mountedByCreator = $true
        }
        $isoVolumes = @($source | Get-Volume -ErrorAction Stop | Where-Object { $_.DriveLetter -match '^[A-Z]$' })
        if ($isoVolumes.Count -ne 1) {
            throw 'Windows did not expose a single readable ISO drive letter.'
        }

        Write-Output 'Checking the boot files, tuner and persistence data...'
        $plan = @(Get-CopyPlan $Payload ('{0}:\' -f $isoVolumes[0].DriveLetter))
        $bytes = ($plan | Measure-Object -Property Bytes -Sum).Sum
        $disk = Get-ConfirmedDisk $Target
        Assert-SourcesOffTarget $disk @($Payload, $isoPath, $Cache)
        $partitionSize = Get-UsbPartitionSize $disk.Size
        if ($partitionSize -lt $bytes + 128MB) { throw 'The selected USB has insufficient room.' }

        Write-Output "Erasing disk $($disk.Number) and creating its FAT32 boot partition..."
        $erasing = $true
        if ($disk.PartitionStyle -ne 'RAW') {
            Clear-Disk -InputObject $disk -RemoveData -RemoveOEM -Confirm:$false -ErrorAction Stop
        }
        $disk = Get-ConfirmedDisk $Target
        Initialize-Disk -InputObject $disk -PartitionStyle MBR -Confirm:$false -ErrorAction Stop
        $disk = Get-ConfirmedDisk $Target
        $partition = New-Partition -DiskNumber $disk.Number -Size $partitionSize -Alignment 1MB -MbrType FAT32 -AssignDriveLetter -ErrorAction Stop
        if ($partition.DriveLetter -notmatch '^[A-Z]$') { throw 'Windows did not assign a USB drive letter.' }
        $letter = $partition.DriveLetter
        $null = Get-ConfirmedDisk $Target
        $volume = Format-Volume -Partition $partition -FileSystem FAT32 -AllocationUnitSize 4096 -NewFileSystemLabel 'WRONGWAY' -Force -Confirm:$false -ErrorAction Stop
        if ($volume.FileSystem -ne 'FAT32' -or $volume.SizeRemaining -lt $bytes) {
            throw 'The formatted USB volume is not FAT32 or is too small.'
        }

        $root = '{0}:\' -f $letter
        $i = 0
        foreach ($file in $plan) {
            $null = Get-ConfirmedDisk $Target
            $current = Get-Partition -DriveLetter $letter -ErrorAction Stop
            if ($current.DiskNumber -ne $Target.Number -or $current.PartitionNumber -ne $partition.PartitionNumber) {
                throw 'The USB drive letter changed while writing.'
            }
            $i++
            Write-Output "Writing and verifying $i of $($plan.Count) - $($file.Name)"
            Copy-UsbFile $file $root
        }
        Write-Output "USB ready at $root. Eject it safely, then boot its x64 UEFI entry."
    } catch {
        if ($erasing) {
            throw "The format or copy did not finish. The USB may be incomplete. $($_.Exception.Message)"
        }
        throw "The USB was not erased. $($_.Exception.Message)"
    } finally {
        if ($mountedByCreator) {
            Dismount-DiskImage -ImagePath $isoPath -ErrorAction SilentlyContinue | Out-Null
        }
    }
}
