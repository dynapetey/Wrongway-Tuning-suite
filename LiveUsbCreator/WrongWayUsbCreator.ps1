if ($env:OS -ne 'Windows_NT') { throw 'Run this creator on Windows 10 or 11.' }
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Windows.Forms
Add-Type -AssemblyName System.Drawing

$identity = [Security.Principal.WindowsIdentity]::GetCurrent()
$principal = [Security.Principal.WindowsPrincipal]::new($identity)
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    try {
        Start-Process -FilePath (Join-Path $PSHOME 'powershell.exe') -Verb RunAs -ArgumentList @(
            '-NoProfile', '-ExecutionPolicy', 'Bypass', '-STA', '-WindowStyle', 'Hidden',
            '-File', ('"{0}"' -f $PSCommandPath))
    } catch {
        [Windows.Forms.MessageBox]::Show('Windows administrator approval is required to format a USB drive.',
            'WrongWay Live USB Creator') | Out-Null
    }
    exit
}

[Windows.Forms.Application]::EnableVisualStyles()
$core = Join-Path $PSScriptRoot 'LiveUsbCore.ps1'
$payload = Join-Path $PSScriptRoot 'payload'
$cache = Join-Path $env:LOCALAPPDATA 'WrongWay\LiveUsb'
if (-not (Test-Path -LiteralPath (Join-Path $payload 'manifest.json') -PathType Leaf)) {
    [Windows.Forms.MessageBox]::Show('Extract the complete creator ZIP to a Windows folder.',
        'WrongWay Live USB Creator') | Out-Null
    exit 1
}
. $core
Import-Module Storage -ErrorAction Stop

$form = [Windows.Forms.Form]::new()
$form.Text = 'WrongWay Live USB Creator'
$form.StartPosition = 'CenterScreen'
$form.ClientSize = [Drawing.Size]::new(730, 450)
$form.MinimumSize = [Drawing.Size]::new(670, 465)
$form.AutoScaleMode = 'Dpi'

$layout = [Windows.Forms.TableLayoutPanel]::new()
$layout.Dock = 'Fill'
$layout.Padding = [Windows.Forms.Padding]::new(18)
$layout.ColumnCount = 2
$layout.RowCount = 7
$null = $layout.ColumnStyles.Add([Windows.Forms.ColumnStyle]::new('Percent', 100))
$null = $layout.ColumnStyles.Add([Windows.Forms.ColumnStyle]::new('Absolute', 107))
foreach ($height in @(51, 25, 37, 25, 37)) {
    $null = $layout.RowStyles.Add([Windows.Forms.RowStyle]::new('Absolute', $height))
}
$null = $layout.RowStyles.Add([Windows.Forms.RowStyle]::new('Percent', 100))
$null = $layout.RowStyles.Add([Windows.Forms.RowStyle]::new('Absolute', 43))
$form.Controls.Add($layout)

$heading = [Windows.Forms.Label]::new()
$heading.Text = "WrongWay Tuning Live`r`nCreate a bootable USB with the tuner and persistent storage."
$heading.Dock = 'Fill'
$heading.Font = [Drawing.Font]::new('Segoe UI', 11)
$layout.Controls.Add($heading, 0, 0)
$layout.SetColumnSpan($heading, 2)

$diskLabel = [Windows.Forms.Label]::new()
$diskLabel.Text = 'USB drive to erase (8 GB or larger)'
$diskLabel.Dock = 'Fill'
$layout.Controls.Add($diskLabel, 0, 1)
$diskList = [Windows.Forms.ComboBox]::new()
$diskList.DropDownStyle = 'DropDownList'
$diskList.DisplayMember = 'Caption'
$diskList.Dock = 'Fill'
$layout.Controls.Add($diskList, 0, 2)
$refresh = [Windows.Forms.Button]::new()
$refresh.Text = 'Refresh'
$refresh.Dock = 'Fill'
$layout.Controls.Add($refresh, 1, 2)

$isoLabel = [Windows.Forms.Label]::new()
$isoLabel.Text = 'Linux Mint 21.1 Xfce 64-bit ISO (leave blank to download automatically)'
$isoLabel.Dock = 'Fill'
$layout.Controls.Add($isoLabel, 0, 3)
$layout.SetColumnSpan($isoLabel, 2)
$isoText = [Windows.Forms.TextBox]::new()
$isoText.Dock = 'Fill'
$layout.Controls.Add($isoText, 0, 4)
$browse = [Windows.Forms.Button]::new()
$browse.Text = 'Browse...'
$browse.Dock = 'Fill'
$layout.Controls.Add($browse, 1, 4)

$activity = [Windows.Forms.TextBox]::new()
$activity.Multiline = $true
$activity.ReadOnly = $true
$activity.ScrollBars = 'Vertical'
$activity.Dock = 'Fill'
$layout.Controls.Add($activity, 0, 5)
$layout.SetColumnSpan($activity, 2)

$create = [Windows.Forms.Button]::new()
$create.Text = 'Create USB'
$create.Dock = 'Fill'
$layout.Controls.Add($create, 1, 6)
$status = [Windows.Forms.Label]::new()
$status.Dock = 'Fill'
$status.TextAlign = 'MiddleLeft'
$status.Text = 'Select the USB drive to continue.'
$layout.Controls.Add($status, 0, 6)
$script:worker = $null
$script:jobError = $null

function Add-Activity {
    param([string]$Line)
    $activity.AppendText($Line + [Environment]::NewLine)
}

function Refresh-Targets {
    $diskList.Items.Clear()
    try {
        foreach ($target in Get-UsbTargets) { $null = $diskList.Items.Add($target) }
        $status.Text = if ($diskList.Items.Count -eq 0) { 'Plug in a writable USB drive and click Refresh.' }
            else { 'Select the USB drive to continue.' }
    } catch {
        $status.Text = 'Could not list USB drives.'
        Add-Activity $_.Exception.Message
    }
    $create.Enabled = $null -ne $diskList.SelectedItem
}

$diskList.Add_SelectedIndexChanged({ $create.Enabled = $null -ne $diskList.SelectedItem })
$refresh.Add_Click({ Refresh-Targets })
$browse.Add_Click({
    $dialog = [Windows.Forms.OpenFileDialog]::new()
    $dialog.Title = 'Select the Linux Mint 21.1 Xfce 64-bit ISO'
    $dialog.Filter = 'ISO images (*.iso)|*.iso'
    try {
        if ($dialog.ShowDialog($form) -eq 'OK') { $isoText.Text = $dialog.FileName }
    } finally { $dialog.Dispose() }
})

$timer = [Windows.Forms.Timer]::new()
$timer.Interval = 400
$timer.Add_Tick({
    if ($null -eq $script:worker) { return }
    $errors = @()
    foreach ($line in Receive-Job -Job $script:worker -ErrorAction SilentlyContinue -ErrorVariable errors) {
        Add-Activity ([string]$line)
    }
    foreach ($problem in $errors) { $script:jobError = $problem.Exception.Message; Add-Activity $script:jobError }
    if ($script:worker.State -notin @('Completed', 'Failed', 'Stopped')) { return }
    $timer.Stop()
    if ($script:worker.State -eq 'Completed' -and -not $script:jobError) {
        $status.Text = 'USB ready. Eject it safely before unplugging.'
        [Windows.Forms.MessageBox]::Show($form,
            'WrongWay Tuning Live was written and verified. Eject the drive in Windows, then select its UEFI entry at startup.',
            $form.Text, 'OK', 'Information') | Out-Null
    } else {
        $status.Text = 'The USB was not completed.'
        $detail = $script:jobError
        if (-not $detail) { $detail = 'Windows stopped the creation job. See the activity log.' }
        [Windows.Forms.MessageBox]::Show($form, $detail, $form.Text, 'OK', 'Error') | Out-Null
    }
    Remove-Job -Job $script:worker -Force
    $script:worker = $null
    $diskList.Enabled = $refresh.Enabled = $isoText.Enabled = $browse.Enabled = $true
    $diskList.SelectedIndex = -1
    $create.Enabled = $false
})

$create.Add_Click({
    $target = $diskList.SelectedItem
    if ($null -eq $target -or $null -ne $script:worker) { return }
    $answer = [Windows.Forms.MessageBox]::Show($form,
        ("Erase every partition and all files on this drive?`r`n`r`n" + $target.Caption),
        'Confirm USB erase', 'YesNo', 'Warning', 'Button2')
    if ($answer -ne 'Yes') { return }

    $activity.Clear()
    $status.Text = 'Preparing the live USB...'
    $script:jobError = $null
    try {
        $script:worker = Start-Job -ArgumentList $core, $target, $payload, $isoText.Text.Trim(), $cache -ScriptBlock {
            param($Core, $Target, $Payload, $Iso, $Cache)
            $ErrorActionPreference = 'Stop'
            . $Core
            Import-Module Storage -ErrorAction Stop
            Install-WrongWayUsb $Target $Payload $Iso $Cache
        }
        $diskList.Enabled = $refresh.Enabled = $isoText.Enabled = $browse.Enabled = $create.Enabled = $false
        $timer.Start()
    } catch {
        $status.Text = 'Could not start the creator.'
        Add-Activity $_.Exception.Message
    }
})

$form.Add_FormClosing({
    param($sender, $event)
    if ($null -ne $script:worker -and $event.CloseReason -eq 'UserClosing') { $event.Cancel = $true }
})
$form.Add_Shown({ Refresh-Targets })
try { [void]$form.ShowDialog() }
finally { $timer.Dispose(); $form.Dispose() }
