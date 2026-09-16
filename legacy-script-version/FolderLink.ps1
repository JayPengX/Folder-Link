#Requires -Version 5.0
<#
    FolderLink
    ----------
    Moves everything inside a chosen "source" folder into a chosen
    "destination" folder, then leaves a symbolic link at the original
    location so any shortcut, saved path, or other program that still
    points at the source folder keeps working transparently.

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
            "建立捷徑需要系統管理員權限，但您取消了權限提升。`n請重新執行本程式，並在使用者帳戶控制視窗中選擇「是」。",
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
$script:LogDir        = Join-Path $env:LOCALAPPDATA 'FolderLink\Logs'
New-Item -ItemType Directory -Path $script:LogDir -Force | Out-Null

function Write-Log {
    param([string]$Text)
    $logBox.AppendText($Text + [Environment]::NewLine)
}

function Set-Status {
    param([string]$Text)
    $statusLabel.Text = "狀態：$Text"
}

function Set-Busy {
    param([bool]$Busy)
    $startButton.Enabled     = -not $Busy
    $sourceBrowse.Enabled    = -not $Busy
    $destBrowse.Enabled      = -not $Busy
    $sourceBox.Enabled       = -not $Busy
    $destBox.Enabled         = -not $Busy
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
        return '請同時選擇來源資料夾與目的資料夾。'
    }
    if (-not (Test-Path -LiteralPath $Source -PathType Container)) {
        return "來源資料夾不存在：`n$Source"
    }

    $srcItem = Get-Item -LiteralPath $Source -Force
    if ($srcItem.Attributes -band [IO.FileAttributes]::ReparsePoint) {
        return "來源資料夾本身已經是捷徑（符號連結或接合點）。`n請選擇實際存放檔案的資料夾。"
    }

    $srcFull = (Get-FullPathSafe $Source).TrimEnd('\') + '\'
    $dstFull = (Get-FullPathSafe $Destination).TrimEnd('\') + '\'

    if ($srcFull -eq $dstFull) {
        return '來源資料夾與目的資料夾不能相同。'
    }
    if ($dstFull.StartsWith($srcFull, [StringComparison]::OrdinalIgnoreCase)) {
        return '目的資料夾不能位於來源資料夾之內（這樣會把資料夾複製到自己裡面）。'
    }
    if ($srcFull.StartsWith($dstFull, [StringComparison]::OrdinalIgnoreCase)) {
        return '來源資料夾不能位於目的資料夾之內。'
    }

    if (Test-Path -LiteralPath $Destination) {
        $dstItem = Get-Item -LiteralPath $Destination -Force
        if ($dstItem.Attributes -band [IO.FileAttributes]::ReparsePoint) {
            return "目的資料夾本身就是捷徑，請選擇實際的資料夾作為目的地。"
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
            return "$destRoot 空間不足`n需要：$needGB GB，可用：$haveGB GB"
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
    param([string]$Source, [string]$Destination)

    $preflightError = Test-PreFlight -Source $Source -Destination $Destination
    if ($preflightError) {
        [System.Windows.Forms.MessageBox]::Show($preflightError, 'FolderLink', 'OK', 'Error') | Out-Null
        return
    }

    $confirm = [System.Windows.Forms.MessageBox]::Show(
        "此操作將會把下列位置的所有檔案與子資料夾：`n$Source`n`n搬移到：`n$Destination`n`n並將原始資料夾替換成指向新位置的符號連結。`n`n是否要繼續？",
        '確認搬移', 'YesNo', 'Question')
    if ($confirm -ne 'Yes') { return }

    New-Item -ItemType Directory -Path $Destination -Force | Out-Null

    $script:PendingSource = $Source
    $script:PendingDest   = $Destination

    $stamp = Get-Date -Format 'yyyyMMdd_HHmmss'
    $script:OutFile = Join-Path $script:LogDir "transfer_$stamp.out.log"
    $script:ErrFile = Join-Path $script:LogDir "transfer_$stamp.err.log"
    $script:LastOutLength = 0

    $logBox.Clear()
    Write-Log "搬移中：`n  來源：$Source`n  目的：$Destination`n"
    Set-Status '正在複製檔案...'
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
        Write-Log "無法啟動 robocopy：$($_.Exception.Message)"
        Set-Status '啟動失敗'
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

    if ($exitCode -ge 8) {
        Write-Log "`nRobocopy 回報發生錯誤（結束代碼：$exitCode）。"
        Write-Log "部分檔案可能正被使用中（鎖定），重試多次後仍無法搬移。"
        Write-Log "已成功複製的檔案，已從來源資料夾中移除。"
        Write-Log "請先關閉正在使用剩餘檔案的程式，再按一次「開始搬移」— 已搬移的檔案不會重複處理。"
        Set-Status '已完成但發生錯誤 — 請查看記錄'
        Set-Busy $false
        [System.Windows.Forms.MessageBox]::Show(
            "部分檔案因正在使用中而無法搬移。`n`n請關閉使用這些檔案的程式，然後重新執行搬移 — 已搬移的檔案不會重複處理。`n`n原始資料夾將維持不變（尚未建立捷徑），因此不會遺失任何檔案。",
            'FolderLink', 'OK', 'Warning') | Out-Null
        return
    }

    Write-Log "`n所有檔案皆已成功複製，正在完成最後步驟..."
    Set-Status '正在完成最後步驟...'

    try {
        $leftover = Get-ChildItem -LiteralPath $Source -Recurse -Force -ErrorAction SilentlyContinue
        if ($leftover) {
            Write-Log "警告：來源資料夾中仍有殘留項目（將保留原資料夾，不建立捷徑）："
            $leftover | ForEach-Object { Write-Log "  $($_.FullName)" }
            Set-Status '已完成但仍有殘留檔案 — 請查看記錄'
            Set-Busy $false
            return
        }

        Remove-Item -LiteralPath $Source -Force -Recurse
        New-Item -ItemType SymbolicLink -Path $Source -Target $Destination -ErrorAction Stop | Out-Null
        Write-Log "捷徑已建立：`n  $Source  -->  $Destination"
        Set-Status '完成'
        [System.Windows.Forms.MessageBox]::Show(
            "搬移完成。`n`n所有檔案現在都位於：`n$Destination`n`n並已在原始位置建立符號連結，讓現有的捷徑能夠繼續正常運作。",
            'FolderLink', 'OK', 'Information') | Out-Null
    } catch {
        Write-Log "完成最後步驟時發生錯誤：$($_.Exception.Message)"
        Set-Status '收尾失敗 — 請查看記錄'
        [System.Windows.Forms.MessageBox]::Show(
            "檔案已搬移完成，但建立捷徑失敗：`n$($_.Exception.Message)`n`n您可以手動建立捷徑，例如：`nmklink /D `"$Source`" `"$Destination`"",
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
$form.Text = 'FolderLink — 搬移資料夾並保留原路徑捷徑'
$form.Size = New-Object System.Drawing.Size(700, 500)
$form.MinimumSize = New-Object System.Drawing.Size(620, 400)
$form.StartPosition = 'CenterScreen'
$form.Font = New-Object System.Drawing.Font('Microsoft JhengHei UI', 9)

$margin = 15
$labelWidth = 110
$boxWidth = 410

# Source row
$sourceLabel = New-Object System.Windows.Forms.Label
$sourceLabel.Text = '來源資料夾：'
$sourceLabel.Location = New-Object System.Drawing.Point($margin, 20)
$sourceLabel.Size = New-Object System.Drawing.Size($labelWidth, 20)

$sourceBox = New-Object System.Windows.Forms.TextBox
$sourceBox.Location = New-Object System.Drawing.Point(($margin + $labelWidth), 18)
$sourceBox.Size = New-Object System.Drawing.Size($boxWidth, 22)
$sourceBox.Anchor = 'Top,Left,Right'

$sourceBrowse = New-Object System.Windows.Forms.Button
$sourceBrowse.Text = '瀏覽...'
$sourceBrowse.Location = New-Object System.Drawing.Point(($margin + $labelWidth + $boxWidth + 10), 17)
$sourceBrowse.Size = New-Object System.Drawing.Size(90, 24)
$sourceBrowse.Anchor = 'Top,Right'

# Destination row
$destLabel = New-Object System.Windows.Forms.Label
$destLabel.Text = '目的資料夾：'
$destLabel.Location = New-Object System.Drawing.Point($margin, 55)
$destLabel.Size = New-Object System.Drawing.Size($labelWidth, 20)

$destBox = New-Object System.Windows.Forms.TextBox
$destBox.Location = New-Object System.Drawing.Point(($margin + $labelWidth), 53)
$destBox.Size = New-Object System.Drawing.Size($boxWidth, 22)
$destBox.Anchor = 'Top,Left,Right'

$destBrowse = New-Object System.Windows.Forms.Button
$destBrowse.Text = '瀏覽...'
$destBrowse.Location = New-Object System.Drawing.Point(($margin + $labelWidth + $boxWidth + 10), 52)
$destBrowse.Size = New-Object System.Drawing.Size(90, 24)
$destBrowse.Anchor = 'Top,Right'

# Buttons row
$startButton = New-Object System.Windows.Forms.Button
$startButton.Text = '開始搬移'
$startButton.Location = New-Object System.Drawing.Point($margin, 95)
$startButton.Size = New-Object System.Drawing.Size(120, 34)
$startButton.Font = New-Object System.Drawing.Font('Microsoft JhengHei UI', 9, [System.Drawing.FontStyle]::Bold)

$cancelButton = New-Object System.Windows.Forms.Button
$cancelButton.Text = '取消'
$cancelButton.Location = New-Object System.Drawing.Point(($margin + 130), 95)
$cancelButton.Size = New-Object System.Drawing.Size(100, 34)
$cancelButton.Enabled = $false

$openLogButton = New-Object System.Windows.Forms.Button
$openLogButton.Text = '開啟記錄資料夾'
$openLogButton.Location = New-Object System.Drawing.Point(($margin + 240), 95)
$openLogButton.Size = New-Object System.Drawing.Size(150, 34)
$openLogButton.Anchor = 'Top,Left'

$statusLabel = New-Object System.Windows.Forms.Label
$statusLabel.Text = '狀態：閒置'
$statusLabel.Location = New-Object System.Drawing.Point($margin, 138)
$statusLabel.Size = New-Object System.Drawing.Size(600, 20)
$statusLabel.Anchor = 'Top,Left,Right'

$progressBar = New-Object System.Windows.Forms.ProgressBar
$progressBar.Location = New-Object System.Drawing.Point($margin, 160)
$progressBar.Size = New-Object System.Drawing.Size(645, 18)
$progressBar.Anchor = 'Top,Left,Right'
$progressBar.Style = 'Blocks'

$logBox = New-Object System.Windows.Forms.TextBox
$logBox.Multiline = $true
$logBox.ScrollBars = 'Vertical'
$logBox.ReadOnly = $true
$logBox.Font = New-Object System.Drawing.Font('Consolas', 9)
$logBox.Location = New-Object System.Drawing.Point($margin, 185)
$logBox.Size = New-Object System.Drawing.Size(645, 250)
$logBox.Anchor = 'Top,Bottom,Left,Right'

$form.Controls.AddRange(@(
    $sourceLabel, $sourceBox, $sourceBrowse,
    $destLabel, $destBox, $destBrowse,
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
    $dlg.Description = '請選擇要搬移檔案的來源資料夾'
    $dlg.ShowNewFolderButton = $false
    if ($dlg.ShowDialog() -eq 'OK') { $sourceBox.Text = $dlg.SelectedPath }
})

$destBrowse.Add_Click({
    $dlg = New-Object System.Windows.Forms.FolderBrowserDialog
    $dlg.Description = '請選擇（或建立）目的資料夾'
    $dlg.ShowNewFolderButton = $true
    if ($dlg.ShowDialog() -eq 'OK') { $destBox.Text = $dlg.SelectedPath }
})

$startButton.Add_Click({
    Start-Transfer -Source $sourceBox.Text.Trim() -Destination $destBox.Text.Trim()
})

$cancelButton.Add_Click({
    if ($script:RoboProcess -and -not $script:RoboProcess.HasExited) {
        try { $script:RoboProcess.Kill() } catch { }
        Write-Log "`n使用者已取消操作。已複製的檔案已從來源移除；其餘未處理的檔案維持原狀，且尚未建立捷徑。"
        Set-Status '已取消'
    }
    $pollTimer.Stop()
    Set-Busy $false
})

$openLogButton.Add_Click({
    Start-Process explorer.exe $script:LogDir
})

$form.Add_FormClosing({
    if ($script:RoboProcess -and -not $script:RoboProcess.HasExited) {
        $r = [System.Windows.Forms.MessageBox]::Show('搬移作業仍在進行中，確定要取消並離開嗎？', 'FolderLink', 'YesNo', 'Warning')
        if ($r -ne 'Yes') { $_.Cancel = $true; return }
        try { $script:RoboProcess.Kill() } catch { }
    }
})

[System.Windows.Forms.Application]::EnableVisualStyles()
[System.Windows.Forms.Application]::Run($form)
