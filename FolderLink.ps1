#Requires -Version 5.0
<#
    FolderLink
    ----------
    Moves everything inside a chosen "source" folder into a chosen
    "destination" folder, then leaves a link (symbolic link or junction) at
    the original location so any shortcut, saved path, or other program that
    still points at the source folder keeps working transparently.

    This is the classic "robocopy /MOVE + mklink" trick, wrapped in a small
    WinForms UI, with retry handling for locked files, disk-space and path
    sanity checks, and a resumable failure mode (re-running the tool after
    closing whatever locked a file will pick up only what's left).

    This script makes no network connections, downloads or runs no remote
    code, and makes no persistence changes (no registry Run keys, no
    scheduled tasks, no startup folder entries). It only touches the two
    folders you explicitly choose in the UI. It requests Administrator
    rights solely because creating a symbolic link requires them.
#>

Add-Type -AssemblyName System.Windows.Forms
Add-Type -AssemblyName System.Drawing

# ---------------------------------------------------------------------------
# Self-elevate to Administrator
# ---------------------------------------------------------------------------
function Test-IsAdmin {
    $identity  = [Security.Principal.WindowsIdentity]::GetCurrent()
    $principal = New-Object Security.Principal.WindowsPrincipal($identity)
    return $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
}

if (-not (Test-IsAdmin)) {
    try {
        $psi = New-Object System.Diagnostics.ProcessStartInfo
        $psi.FileName  = (Get-Process -Id $PID).Path
        $psi.Arguments = "-NoProfile -ExecutionPolicy Bypass -File `"$PSCommandPath`""
        $psi.Verb      = 'runas'
        [System.Diagnostics.Process]::Start($psi) | Out-Null
    } catch {
        [System.Windows.Forms.MessageBox]::Show(
            "Administrator permission is required to create links, and elevation was cancelled.`nPlease run this tool again and accept the UAC prompt.",
            'FolderLink', 'OK', 'Warning') | Out-Null
    }
    exit
}

# ---------------------------------------------------------------------------
# State
# ---------------------------------------------------------------------------
$script:RoboProcess   = $null
$script:OutFile       = $null
$script:ErrFile       = $null
$script:LastOutLength = 0
$script:PendingSource = $null
$script:PendingDest   = $null
$script:PendingLink   = 'Symbolic'
$script:LogDir        = Join-Path $env:LOCALAPPDATA 'FolderLink\Logs'
New-Item -ItemType Directory -Path $script:LogDir -Force | Out-Null

function Write-Log {
    param([string]$Text)
    $logBox.AppendText($Text + [Environment]::NewLine)
}

function Set-Status {
    param([string]$Text)
    $statusLabel.Text = "Status: $Text"
}

function Set-Busy {
    param([bool]$Busy)
    $startButton.Enabled     = -not $Busy
    $sourceBrowse.Enabled    = -not $Busy
    $destBrowse.Enabled      = -not $Busy
    $sourceBox.Enabled       = -not $Busy
    $destBox.Enabled         = -not $Busy
    $symLinkRadio.Enabled    = -not $Busy
    $junctionRadio.Enabled   = -not $Busy
    $cancelButton.Enabled    = $Busy
    if ($Busy) { $progressBar.Style = 'Marquee' } else { $progressBar.Style = 'Blocks'; $progressBar.Value = 0 }
}

# ---------------------------------------------------------------------------
# Pre-flight validation
# ---------------------------------------------------------------------------
function Get-FullPathSafe {
    param([string]$Path)
    return [System.IO.Path]::GetFullPath($Path)
}

function Test-PreFlight {
    param([string]$Source, [string]$Destination)

    if ([string]::IsNullOrWhiteSpace($Source) -or [string]::IsNullOrWhiteSpace($Destination)) {
        return 'Please choose both a source folder and a destination folder.'
    }
    if (-not (Test-Path -LiteralPath $Source -PathType Container)) {
        return "Source folder does not exist:`n$Source"
    }

    $srcItem = Get-Item -LiteralPath $Source -Force
    if ($srcItem.Attributes -band [IO.FileAttributes]::ReparsePoint) {
        return "The source folder is already a link (symbolic link or junction).`nPick the real folder that still contains files."
    }

    $srcFull = (Get-FullPathSafe $Source).TrimEnd('\') + '\'
    $dstFull = (Get-FullPathSafe $Destination).TrimEnd('\') + '\'

    if ($srcFull -eq $dstFull) {
        return 'Source and destination cannot be the same folder.'
    }
    if ($dstFull.StartsWith($srcFull, [StringComparison]::OrdinalIgnoreCase)) {
        return 'The destination folder cannot be inside the source folder (that would copy the folder into itself).'
    }
    if ($srcFull.StartsWith($dstFull, [StringComparison]::OrdinalIgnoreCase)) {
        return 'The source folder cannot be inside the destination folder.'
    }

    if (Test-Path -LiteralPath $Destination) {
        $dstItem = Get-Item -LiteralPath $Destination -Force
        if ($dstItem.Attributes -band [IO.FileAttributes]::ReparsePoint) {
            return "The destination folder is itself a link. Choose a real folder as the destination."
        }
    }

    # Disk space check
    try {
        $totalBytes = (Get-ChildItem -LiteralPath $Source -Recurse -Force -File -ErrorAction SilentlyContinue |
                       Measure-Object -Property Length -Sum).Sum
        if (-not $totalBytes) { $totalBytes = 0 }

        $destRoot = [System.IO.Path]::GetPathRoot((Get-FullPathSafe $Destination))
        $drive = New-Object System.IO.DriveInfo($destRoot)
        if ($drive.IsReady -and $drive.AvailableFreeSpace -lt $totalBytes) {
            $needGB  = [Math]::Round($totalBytes / 1GB, 2)
            $haveGB  = [Math]::Round($drive.AvailableFreeSpace / 1GB, 2)
            return "Not enough free space on $destRoot`nNeeded: $needGB GB, Available: $haveGB GB"
        }
    } catch {
        # Non-fatal — if the space check itself fails, let robocopy be the final judge.
    }

    return $null
}

# ---------------------------------------------------------------------------
# Robocopy driven move
# ---------------------------------------------------------------------------
function Start-Transfer {
    param([string]$Source, [string]$Destination, [string]$LinkType)

    $preflightError = Test-PreFlight -Source $Source -Destination $Destination
    if ($preflightError) {
        [System.Windows.Forms.MessageBox]::Show($preflightError, 'FolderLink', 'OK', 'Error') | Out-Null
        return
    }

    $confirm = [System.Windows.Forms.MessageBox]::Show(
        "This will move ALL files and subfolders from:`n$Source`n`nto:`n$Destination`n`nand replace the original folder with a $LinkType link pointing to the new location.`n`nContinue?",
        'Confirm Transfer', 'YesNo', 'Question')
    if ($confirm -ne 'Yes') { return }

    New-Item -ItemType Directory -Path $Destination -Force | Out-Null

    $script:PendingSource = $Source
    $script:PendingDest   = $Destination
    $script:PendingLink   = $LinkType

    $stamp = Get-Date -Format 'yyyyMMdd_HHmmss'
    $script:OutFile = Join-Path $script:LogDir "transfer_$stamp.out.log"
    $script:ErrFile = Join-Path $script:LogDir "transfer_$stamp.err.log"
    $script:LastOutLength = 0

    $logBox.Clear()
    Write-Log "Moving:`n  From: $Source`n  To:   $Destination`n"
    Set-Status 'Copying files...'
    Set-Busy $true

    # /MOVE   move files & dirs, delete from source once copied
    # /E      include subfolders, including empty ones
    # /IS     include files that already look identical, so they are still moved (not silently left behind)
    # /R:5 /W:5   retry a locked/in-use file 5 times, 5 seconds apart, then move on
    # /XJ     do not follow junctions/symlinks found inside the source (avoids loops/duplication)
    # /MT:8   copy up to 8 files in parallel
    # /NFL /NDL /NP   quieter, more readable log output
    $argList = @(
        ('"{0}"' -f $Source),
        ('"{0}"' -f $Destination),
        '/MOVE', '/E', '/IS', '/R:5', '/W:5', '/XJ', '/MT:8', '/NFL', '/NDL', '/NP'
    )

    try {
        # Redirecting straight to files (rather than reading the process's
        # stdout/stderr pipes ourselves) avoids pipe-buffer deadlocks and
        # lets a simple UI timer just tail the growing file. Minimized
        # (not hidden) so the console window robocopy briefly owns is
        # still visible in the taskbar if anyone wants to check on it.
        $script:RoboProcess = Start-Process -FilePath 'robocopy.exe' -ArgumentList $argList `
            -WindowStyle Minimized -PassThru `
            -RedirectStandardOutput $script:OutFile -RedirectStandardError $script:ErrFile
    } catch {
        Write-Log "Failed to start robocopy: $($_.Exception.Message)"
        Set-Status 'Failed to start'
        Set-Busy $false
        return
    }

    $pollTimer.Start()
}

function Complete-Transfer {
    $exitCode = $script:RoboProcess.ExitCode
    Tail-Output

    $Source      = $script:PendingSource
    $Destination = $script:PendingDest
    $LinkType    = $script:PendingLink

    if ($exitCode -ge 8) {
        Write-Log "`nRobocopy reported errors (exit code $exitCode)."
        Write-Log "Some files were likely in use (locked) and could not be moved after retries."
        Write-Log "Files that were successfully copied have already been removed from the source."
        Write-Log "Close whatever program is using the remaining file(s) and click Start Transfer again — already-moved files will not be duplicated."
        Set-Status 'Completed with errors — see log'
        Set-Busy $false
        [System.Windows.Forms.MessageBox]::Show(
            "Some files could not be moved because they were in use.`n`nClose the program using them and run the transfer again — files already moved will not be duplicated.`n`nThe original folder was left in place (not linked) so nothing is lost.",
            'FolderLink', 'OK', 'Warning') | Out-Null
        return
    }

    Write-Log "`nAll files copied successfully. Finalizing..."
    Set-Status 'Finalizing...'

    try {
        $leftover = Get-ChildItem -LiteralPath $Source -Recurse -Force -ErrorAction SilentlyContinue
        if ($leftover) {
            Write-Log "Warning: some items remain in the source folder (leaving it in place instead of linking it):"
            $leftover | ForEach-Object { Write-Log "  $($_.FullName)" }
            Set-Status 'Completed with leftovers — see log'
            Set-Busy $false
            return
        }

        Remove-Item -LiteralPath $Source -Force -Recurse
        if ($LinkType -eq 'Symbolic') {
            New-Item -ItemType SymbolicLink -Path $Source -Target $Destination -ErrorAction Stop | Out-Null
        } else {
            New-Item -ItemType Junction -Path $Source -Target $Destination -ErrorAction Stop | Out-Null
        }
        Write-Log "Link created:`n  $Source  -->  $Destination"
        Set-Status 'Done'
        [System.Windows.Forms.MessageBox]::Show(
            "Transfer complete.`n`nAll files now live at:`n$Destination`n`nand a $LinkType link was left at the original location so existing shortcuts keep working.",
            'FolderLink', 'OK', 'Information') | Out-Null
    } catch {
        Write-Log "Error while finalizing: $($_.Exception.Message)"
        Set-Status 'Failed while finalizing — see log'
        [System.Windows.Forms.MessageBox]::Show(
            "Files were moved, but creating the link failed:`n$($_.Exception.Message)`n`nYou can create it manually, e.g.:`nmklink /J `"$Source`" `"$Destination`"",
            'FolderLink', 'OK', 'Error') | Out-Null
    }

    Set-Busy $false
}

function Tail-Output {
    try {
        if (Test-Path -LiteralPath $script:OutFile) {
            $content = Get-Content -LiteralPath $script:OutFile -Raw -ErrorAction SilentlyContinue
            if ($content -and $content.Length -gt $script:LastOutLength) {
                $newText = $content.Substring($script:LastOutLength)
                Write-Log $newText.TrimEnd()
                $script:LastOutLength = $content.Length
            }
        }
    } catch { }
}

# ---------------------------------------------------------------------------
# UI
# ---------------------------------------------------------------------------
$form = New-Object System.Windows.Forms.Form
$form.Text = 'FolderLink — Move a folder and leave a link behind'
$form.Size = New-Object System.Drawing.Size(700, 560)
$form.MinimumSize = New-Object System.Drawing.Size(620, 460)
$form.StartPosition = 'CenterScreen'
$form.Font = New-Object System.Drawing.Font('Segoe UI', 9)

$margin = 15
$labelWidth = 90
$boxWidth = 430

# Source row
$sourceLabel = New-Object System.Windows.Forms.Label
$sourceLabel.Text = 'Source folder:'
$sourceLabel.Location = New-Object System.Drawing.Point($margin, 20)
$sourceLabel.Size = New-Object System.Drawing.Size($labelWidth, 20)

$sourceBox = New-Object System.Windows.Forms.TextBox
$sourceBox.Location = New-Object System.Drawing.Point(($margin + $labelWidth), 18)
$sourceBox.Size = New-Object System.Drawing.Size($boxWidth, 22)
$sourceBox.Anchor = 'Top,Left,Right'

$sourceBrowse = New-Object System.Windows.Forms.Button
$sourceBrowse.Text = 'Browse...'
$sourceBrowse.Location = New-Object System.Drawing.Point(($margin + $labelWidth + $boxWidth + 10), 17)
$sourceBrowse.Size = New-Object System.Drawing.Size(90, 24)
$sourceBrowse.Anchor = 'Top,Right'

# Destination row
$destLabel = New-Object System.Windows.Forms.Label
$destLabel.Text = 'Destination folder:'
$destLabel.Location = New-Object System.Drawing.Point($margin, 55)
$destLabel.Size = New-Object System.Drawing.Size($labelWidth, 20)

$destBox = New-Object System.Windows.Forms.TextBox
$destBox.Location = New-Object System.Drawing.Point(($margin + $labelWidth), 53)
$destBox.Size = New-Object System.Drawing.Size($boxWidth, 22)
$destBox.Anchor = 'Top,Left,Right'

$destBrowse = New-Object System.Windows.Forms.Button
$destBrowse.Text = 'Browse...'
$destBrowse.Location = New-Object System.Drawing.Point(($margin + $labelWidth + $boxWidth + 10), 52)
$destBrowse.Size = New-Object System.Drawing.Size(90, 24)
$destBrowse.Anchor = 'Top,Right'

# Link type
$linkGroup = New-Object System.Windows.Forms.GroupBox
$linkGroup.Text = 'Link type left at the original location'
$linkGroup.Location = New-Object System.Drawing.Point($margin, 90)
$linkGroup.Size = New-Object System.Drawing.Size(645, 55)
$linkGroup.Anchor = 'Top,Left,Right'

$symLinkRadio = New-Object System.Windows.Forms.RadioButton
$symLinkRadio.Text = 'Symbolic link (recommended — works across drives and network paths)'
$symLinkRadio.Location = New-Object System.Drawing.Point(15, 15)
$symLinkRadio.Size = New-Object System.Drawing.Size(600, 20)
$symLinkRadio.Checked = $true

$junctionRadio = New-Object System.Windows.Forms.RadioButton
$junctionRadio.Text = 'Junction (local drives only, no admin needed on its own)'
$junctionRadio.Location = New-Object System.Drawing.Point(15, 32)
$junctionRadio.Size = New-Object System.Drawing.Size(600, 20)

$linkGroup.Controls.AddRange(@($symLinkRadio, $junctionRadio))

# Buttons row
$startButton = New-Object System.Windows.Forms.Button
$startButton.Text = 'Start Transfer'
$startButton.Location = New-Object System.Drawing.Point($margin, 155)
$startButton.Size = New-Object System.Drawing.Size(140, 34)
$startButton.Font = New-Object System.Drawing.Font('Segoe UI', 9, [System.Drawing.FontStyle]::Bold)

$cancelButton = New-Object System.Windows.Forms.Button
$cancelButton.Text = 'Cancel'
$cancelButton.Location = New-Object System.Drawing.Point(($margin + 150), 155)
$cancelButton.Size = New-Object System.Drawing.Size(100, 34)
$cancelButton.Enabled = $false

$openLogButton = New-Object System.Windows.Forms.Button
$openLogButton.Text = 'Open Log Folder'
$openLogButton.Location = New-Object System.Drawing.Point(($margin + 260), 155)
$openLogButton.Size = New-Object System.Drawing.Size(130, 34)
$openLogButton.Anchor = 'Top,Left'

$statusLabel = New-Object System.Windows.Forms.Label
$statusLabel.Text = 'Status: Idle'
$statusLabel.Location = New-Object System.Drawing.Point($margin, 198)
$statusLabel.Size = New-Object System.Drawing.Size(600, 20)
$statusLabel.Anchor = 'Top,Left,Right'

$progressBar = New-Object System.Windows.Forms.ProgressBar
$progressBar.Location = New-Object System.Drawing.Point($margin, 220)
$progressBar.Size = New-Object System.Drawing.Size(645, 18)
$progressBar.Anchor = 'Top,Left,Right'
$progressBar.Style = 'Blocks'

$logBox = New-Object System.Windows.Forms.TextBox
$logBox.Multiline = $true
$logBox.ScrollBars = 'Vertical'
$logBox.ReadOnly = $true
$logBox.Font = New-Object System.Drawing.Font('Consolas', 9)
$logBox.Location = New-Object System.Drawing.Point($margin, 245)
$logBox.Size = New-Object System.Drawing.Size(645, 250)
$logBox.Anchor = 'Top,Bottom,Left,Right'

$form.Controls.AddRange(@(
    $sourceLabel, $sourceBox, $sourceBrowse,
    $destLabel, $destBox, $destBrowse,
    $linkGroup,
    $startButton, $cancelButton, $openLogButton,
    $statusLabel, $progressBar, $logBox
))

# ---------------------------------------------------------------------------
# Timer used to poll the robocopy process without freezing the UI
# ---------------------------------------------------------------------------
$pollTimer = New-Object System.Windows.Forms.Timer
$pollTimer.Interval = 400
$pollTimer.Add_Tick({
    Tail-Output
    if ($script:RoboProcess -and $script:RoboProcess.HasExited) {
        $pollTimer.Stop()
        Complete-Transfer
    }
})

# ---------------------------------------------------------------------------
# Event handlers
# ---------------------------------------------------------------------------
$sourceBrowse.Add_Click({
    $dlg = New-Object System.Windows.Forms.FolderBrowserDialog
    $dlg.Description = 'Choose the folder whose files should be moved'
    $dlg.ShowNewFolderButton = $false
    if ($dlg.ShowDialog() -eq 'OK') { $sourceBox.Text = $dlg.SelectedPath }
})

$destBrowse.Add_Click({
    $dlg = New-Object System.Windows.Forms.FolderBrowserDialog
    $dlg.Description = 'Choose (or create) the destination folder'
    $dlg.ShowNewFolderButton = $true
    if ($dlg.ShowDialog() -eq 'OK') { $destBox.Text = $dlg.SelectedPath }
})

$startButton.Add_Click({
    $linkType = if ($symLinkRadio.Checked) { 'Symbolic' } else { 'Junction' }
    Start-Transfer -Source $sourceBox.Text.Trim() -Destination $destBox.Text.Trim() -LinkType $linkType
})

$cancelButton.Add_Click({
    if ($script:RoboProcess -and -not $script:RoboProcess.HasExited) {
        try { $script:RoboProcess.Kill() } catch { }
        Write-Log "`nCancelled by user. Files already copied were removed from the source; anything left behind was not touched, and no link was created."
        Set-Status 'Cancelled'
    }
    $pollTimer.Stop()
    Set-Busy $false
})

$openLogButton.Add_Click({
    Start-Process explorer.exe $script:LogDir
})

$form.Add_FormClosing({
    if ($script:RoboProcess -and -not $script:RoboProcess.HasExited) {
        $r = [System.Windows.Forms.MessageBox]::Show('A transfer is still running. Cancel it and exit?', 'FolderLink', 'YesNo', 'Warning')
        if ($r -ne 'Yes') { $_.Cancel = $true; return }
        try { $script:RoboProcess.Kill() } catch { }
    }
})

[System.Windows.Forms.Application]::EnableVisualStyles()
[System.Windows.Forms.Application]::Run($form)
