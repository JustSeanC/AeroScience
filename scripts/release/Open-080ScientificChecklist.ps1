#Requires -Version 7.0
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$ValidationFolder,
    [Parameter(Mandatory)][ValidatePattern('^[0-9a-f]{40}$')][string]$ExpectedSourceCommit,
    [Parameter(Mandatory)][ValidatePattern('^[0-9a-fA-F]{64}$')][string]$ExpectedReceiptSha256
)
$ErrorActionPreference='Stop'
if(-not $IsWindows){throw 'Open this separate P08c checklist on your Windows test PC. Your tested app does not need rebuilding.'}
Add-Type -AssemblyName System.Windows.Forms
Add-Type -AssemblyName System.Drawing
[Windows.Forms.Application]::EnableVisualStyles()
$repo=(Resolve-Path "$PSScriptRoot/../..").Path

function Import-P08cChecklistControls {
    # Reuse only the existing trusted form constructors, never its top-level
    # launch/save code or P08b data backend. Original P08b source is unchanged.
    $tokens=$null;$errors=$null
    $ast=[Management.Automation.Language.Parser]::ParseFile("$repo/scripts/release/Open-080Checklist.ps1",[ref]$tokens,[ref]$errors)
    if($errors){throw 'The existing form controls could not be parsed.'}
    $names=@('New-080ChecklistForm','Set-080ChecklistRows')
    $functions=$ast.FindAll({param($node) $node -is [Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -in $names},$true)
    if($functions.Count -ne 2){throw 'Expected shared form constructors are missing.'}
    foreach($function in $functions){. ([ScriptBlock]::Create($function.Extent.Text));Set-Item "Function:script:$($function.Name)" (Get-Item "Function:$($function.Name)").ScriptBlock}
}

function Invoke-P08cChecklistData([string]$Action,[object]$Request=$null) {
    $start=[Diagnostics.ProcessStartInfo]::new();$start.FileName=(Get-Command python -ErrorAction Stop).Source
    foreach($argument in @("$repo/scripts/release/checklist_p08c.py",$Action,'--folder',$ValidationFolder,'--source',$ExpectedSourceCommit,'--receipt-hash',$ExpectedReceiptSha256.ToLowerInvariant())){$start.ArgumentList.Add($argument)}
    $start.UseShellExecute=$false;$start.CreateNoWindow=$true
    $start.RedirectStandardInput=$true;$start.RedirectStandardOutput=$true;$start.RedirectStandardError=$true
    $start.StandardInputEncoding=[Text.UTF8Encoding]::new($false);$start.StandardOutputEncoding=[Text.UTF8Encoding]::new($false);$start.StandardErrorEncoding=[Text.UTF8Encoding]::new($false)
    $process=[Diagnostics.Process]::new();$process.StartInfo=$start
    try {
        [void]$process.Start();$output=$process.StandardOutput.ReadToEndAsync();$failure=$process.StandardError.ReadToEndAsync()
        if($null -ne $Request){$process.StandardInput.Write(($Request|ConvertTo-Json -Depth 30 -Compress))};$process.StandardInput.Close()
        while(-not $process.WaitForExit(100)){[Windows.Forms.Application]::DoEvents()}
        $text=$output.GetAwaiter().GetResult();$errorText=$failure.GetAwaiter().GetResult()
        if($process.ExitCode -ne 0){throw ($errorText.Trim()+"`r`nEarlier records are preserved. Reopen to see what actually saved.")}
        return $text|ConvertFrom-Json
    } finally {$process.Dispose()}
}

function Set-P08cChecklistRows($View,$State) {
    Set-080ChecklistRows $View $State
    foreach($row in $View.Grid.Rows){$row.Cells[3].Value=[string]$View.Initial[[string]$row.Tag].evidence}
}

function Get-P08cChecklistRequest($View) {
    [void]$View.Grid.EndEdit();$entries=@()
    foreach($row in $View.Grid.Rows){
        $id=[string]$row.Tag;$old=$View.Initial[$id]
        $outcome=[string]$row.Cells[1].Value;$notes=[string]$row.Cells[2].Value;$evidence=[string]$row.Cells[3].Value
        if($outcome -ceq [string]$old.outcome -and $notes -ceq [string]$old.notes -and $evidence -ceq [string]$old.evidence){continue}
        $entries+=@{id=$id;outcome=$outcome;notes=$notes;evidence=$evidence;revision=$old.revision}
    }
    return @{entries=$entries;observed=[bool]$View.Confirmation.Checked}
}

function Get-080ChecklistRequestForClose($View) {return ((Get-P08cChecklistRequest $View).entries.Count -gt 0)}

function New-P08cChecklistForm($State) {
    $view=New-080ChecklistForm $State
    $view.Form.Text='AeroScience 0.8 — Science and longer-flight checks (P08c)'
    $view.Form.AccessibleName='Separate P08c scientific validation checklist'
    $intro=$view.Form.Controls[0].GetControlFromPosition(0,0)
    $intro.Text="Your earlier P08b results stay unchanged. Pick a result, describe what you checked and attach measured evidence where requested.`r`nScientific receipts identify their own tested development build; they do not pass installed Windows/MSFS checks.`r`nNative checks use your existing verified build $($State.source_commit.Substring(0,7)). Saving issues no flight, science, sharing or download commands."
    $view.Confirmation.Text='I performed these checks or reviewed the attached measurements for their explicitly named build.'
    $view.Confirmation.AccessibleName=$view.Confirmation.Text
    $evidence=[Windows.Forms.DataGridViewTextBoxColumn]::new();$evidence.HeaderText='Measured receipt';$evidence.ReadOnly=$true;$evidence.FillWeight=30;$evidence.DefaultCellStyle.WrapMode='True'
    [void]$view.Grid.Columns.Add($evidence)
    $footer=$view.Save.Parent
    $attach=[Windows.Forms.Button]::new();$attach.Text='Attach evidence to selected check';$attach.AutoSize=$true;$attach.AccessibleName=$attach.Text
    $timer=[Windows.Forms.Button]::new();$timer.Text='Open longer-flight timer';$timer.AutoSize=$true;$timer.AccessibleName=$timer.Text
    $footer.Controls.Add($attach);$footer.Controls.Add($timer)
    $view|Add-Member NoteProperty Attach $attach;$view|Add-Member NoteProperty FlightTimer $timer
    Set-P08cChecklistRows $view $State
    $attach.Add_Click({
        $dialog=[Windows.Forms.OpenFileDialog]::new();$dialog.Title='Choose the actual versioned measured receipt (JSON)';$dialog.Filter='Measured receipts (*.json)|*.json';$dialog.CheckFileExists=$true
        try{if($dialog.ShowDialog($view.Form) -eq 'OK'){$view.Grid.CurrentRow.Cells[3].Value=$dialog.FileName;$view.Status.Text='Receipt selected. Save checks its identity, measured contents and artifact hashes.'}}finally{$dialog.Dispose()}
    }.GetNewClosure())
    $timer.Add_Click({try{Open-P08cFlightTimer $view}catch{[void][Windows.Forms.MessageBox]::Show($_.Exception.Message,'Flight timer','OK','Warning')}}.GetNewClosure())
    return $view
}

function Open-P08cFlightTimer($ParentView) {
    $expected=[IO.Path]::GetFullPath((Join-Path $ValidationFolder 'AeroScience.exe'))
    $observedProcesses=@(Get-Process -Name AeroScience -ErrorAction SilentlyContinue|Where-Object {$_.Path -and [IO.Path]::GetFullPath($_.Path) -eq $expected})
    if($observedProcesses.Count -ne 1){throw 'Start the exact verified test companion yourself, then open the timer. This tool never starts or stops it.'}
    $observed=$observedProcesses[0];$processStarted=$observed.StartTime.ToUniversalTime();$initialCpu=$observed.TotalProcessorTime.TotalSeconds
    $form=[Windows.Forms.Form]::new();$form.Text='P08c — Observe my real longer flight';$form.Size=[Drawing.Size]::new(660,410);$form.MinimumSize=[Drawing.Size]::new(580,360);$form.AutoScaleMode='Font';$form.Font=[Drawing.SystemFonts]::MessageBoxFont
    $layout=[Windows.Forms.FlowLayoutPanel]::new();$layout.Dock='Fill';$layout.FlowDirection='TopDown';$layout.WrapContents=$false;$layout.AutoScroll=$true;$layout.Padding=[Windows.Forms.Padding]::new(16);$form.Controls.Add($layout)
    $help=[Windows.Forms.Label]::new();$help.AutoSize=$true;$help.MaximumSize=[Drawing.Size]::new(560,0)
    $help.Text="This only reads your verified companion's CPU/memory counters. It issues no commands.`r`nFly for at least 60 minutes, use your normal science/session controls, end the flight yourself and export its Research Package. Then click Save and select that ZIP.`r`nShorter runs can be saved, but cannot pass the longer-flight check. GPU and network remain unmeasured."
    $label=[Windows.Forms.Label]::new();$label.AutoSize=$true;$label.AccessibleName='Elapsed observation and sampled resources'
    $save=[Windows.Forms.Button]::new();$save.Text='Stop timer and select completed Research Package';$save.AutoSize=$true;$save.AccessibleName=$save.Text
    $layout.Controls.AddRange(@($help,$label,$save))
    $capture=@{schema='aeroscience-p08c-native-observation-1';process_id=$observed.Id;process_path=$expected;process_identity_stable=$true;process_started_utc=$processStarted.ToString('o');started_utc=[DateTimeOffset]::UtcNow.ToString('o');sample_count=0;sampled_peak_working_set_bytes=0L;cpu_seconds=0.0}
    $watch=[Diagnostics.Stopwatch]::StartNew();$clock=[Windows.Forms.Timer]::new();$clock.Interval=1000
    $clock.Add_Tick({
        try {
            $observed.Refresh();if($observed.HasExited -or $observed.StartTime.ToUniversalTime() -ne $processStarted){throw 'The companion stopped/restarted. Keep this observation as incomplete and start a new timer.'}
            $capture.sample_count++;$capture.sampled_peak_working_set_bytes=[Math]::Max($capture.sampled_peak_working_set_bytes,$observed.WorkingSet64)
            $capture.cpu_seconds=$observed.TotalProcessorTime.TotalSeconds-$initialCpu
            $label.Text=('Observed {0:hh\:mm\:ss}; {1} samples; peak sampled RAM {2:N0} MiB' -f $watch.Elapsed,$capture.sample_count,($capture.sampled_peak_working_set_bytes/1MB))
        } catch {$capture.process_identity_stable=$false;$clock.Stop();$watch.Stop();$save.Enabled=$false;$label.Text=$_.Exception.Message}
    }.GetNewClosure())
    $save.Add_Click({
        $clock.Stop();$watch.Stop();$capture.duration_seconds=$watch.Elapsed.TotalSeconds;$capture.finished_utc=[DateTimeOffset]::UtcNow.ToString('o')
        $dialog=[Windows.Forms.OpenFileDialog]::new();$dialog.Title='Select the completed flight Research Package ZIP';$dialog.Filter='Research Package (*.zip)|*.zip';$dialog.CheckFileExists=$true
        try {
            if($dialog.ShowDialog($form) -ne 'OK'){$save.Enabled=$false;$label.Text='Timer stopped. No receipt saved. Close and start a new observation when ready.';return}
            $capture.package_path=$dialog.FileName;$saved=Invoke-P08cChecklistData 'endurance' $capture
            foreach($row in $ParentView.Grid.Rows){if([string]$row.Tag -eq 'native-endurance'){$row.Cells[3].Value=$saved.evidence_file}}
            $ParentView.Status.Text='Timed evidence saved locally. Add actual flight notes and choose a result separately.';$form.Close()
        } catch {$save.Enabled=$false;$label.Text=$_.Exception.Message} finally {$dialog.Dispose()}
    }.GetNewClosure())
    $clock.Start()
    try{[void]$form.ShowDialog($ParentView.Form)}finally{$clock.Stop();$clock.Dispose();$watch.Stop();$form.Dispose();$observed.Dispose()}
}

Import-P08cChecklistControls
$state=Invoke-P08cChecklistData 'load';$view=New-P08cChecklistForm $state
$view.Save.Add_Click({
    try {
        $request=Get-P08cChecklistRequest $view;if(-not $request.entries.Count){$view.Status.Text='No changed checks.';return}
        $view.Busy=$true;$view.Form.UseWaitCursor=$true;$view.Save.Enabled=$false;$view.Close.Enabled=$false;$view.Grid.Enabled=$false;$view.Attach.Enabled=$false;$view.FlightTimer.Enabled=$false
        $saved=Invoke-P08cChecklistData 'save' $request;Set-P08cChecklistRows $view $saved.state;$view.Status.Text="Saved $($saved.saved) separate P08c check(s). P08b records are unchanged."
    } catch {[void][Windows.Forms.MessageBox]::Show($_.Exception.Message,'Check measured evidence','OK','Warning')}
    finally {$view.Busy=$false;$view.Form.UseWaitCursor=$false;$view.Save.Enabled=$true;$view.Close.Enabled=$true;$view.Grid.Enabled=$true;$view.Attach.Enabled=$true;$view.FlightTimer.Enabled=$true}
})
try{[void]$view.Form.ShowDialog()}finally{$view.Form.Dispose()}
