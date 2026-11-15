#Requires -Version 7.0
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$ValidationFolder,
    [Parameter(Mandatory)][ValidatePattern('^[0-9a-f]{40}$')][string]$ExpectedSourceCommit,
    [Parameter(Mandatory)][ValidatePattern('^[0-9a-fA-F]{64}$')][string]$ExpectedReceiptSha256
)
$ErrorActionPreference='Stop'
if(-not $IsWindows){throw 'Open this checklist on the Windows PC containing your existing test build. No rebuild is needed.'}
Add-Type -AssemblyName System.Windows.Forms
Add-Type -AssemblyName System.Drawing
[Windows.Forms.Application]::EnableVisualStyles()
$repo=(Resolve-Path "$PSScriptRoot/../..").Path

function Invoke-080ChecklistData([string]$Action,[object]$Request=$null) {
    $start=[Diagnostics.ProcessStartInfo]::new()
    $start.FileName=(Get-Command python -ErrorAction Stop).Source
    foreach($argument in @("$repo/scripts/release/checklist_080.py",$Action,'--folder',$ValidationFolder,
        '--source',$ExpectedSourceCommit,'--receipt-hash',$ExpectedReceiptSha256.ToLowerInvariant())){
        $start.ArgumentList.Add($argument)
    }
    $start.UseShellExecute=$false;$start.CreateNoWindow=$true
    $start.RedirectStandardInput=$true;$start.RedirectStandardOutput=$true;$start.RedirectStandardError=$true
    $start.StandardInputEncoding=[Text.UTF8Encoding]::new($false)
    $start.StandardOutputEncoding=[Text.UTF8Encoding]::new($false)
    $start.StandardErrorEncoding=[Text.UTF8Encoding]::new($false)
    $process=[Diagnostics.Process]::new();$process.StartInfo=$start
    try {
        [void]$process.Start()
        $output=$process.StandardOutput.ReadToEndAsync();$errorOutput=$process.StandardError.ReadToEndAsync()
        if($null -ne $Request){$process.StandardInput.Write(($Request|ConvertTo-Json -Depth 20 -Compress))}
        $process.StandardInput.Close()
        while(-not $process.WaitForExit(100)){[Windows.Forms.Application]::DoEvents()}
        $text=$output.GetAwaiter().GetResult();$failure=$errorOutput.GetAwaiter().GetResult()
        if($process.ExitCode -ne 0){throw ($failure.Trim()+"`r`nYour earlier notes have been kept. Reopen the checklist to see any notes already saved.")}
        return $text|ConvertFrom-Json
    } finally {$process.Dispose()}
}

function Set-080ChecklistRows($View,$State) {
    $selected=if($null -ne $View.Grid.CurrentCell){$View.Grid.CurrentCell.RowIndex}else{0}
    $View.Grid.Rows.Clear();$View.Initial=@{};$View.State=$State
    foreach($item in $State.rows){
        $index=$View.Grid.Rows.Add([string]$item.title,[string]$item.outcome,[string]$item.notes)
        $View.Grid.Rows[$index].Tag=[string]$item.id
        $View.Initial[[string]$item.id]=$item
    }
    if($View.Grid.Rows.Count){$View.Grid.CurrentCell=$View.Grid.Rows[[Math]::Min($selected,$View.Grid.Rows.Count-1)].Cells[1]}
    $View.Confirmation.Checked=$false
}

function Get-080ChecklistRequest($View) {
    [void]$View.Grid.EndEdit()
    $entries=@()
    foreach($row in $View.Grid.Rows){
        $id=[string]$row.Tag;$original=$View.Initial[$id]
        $outcome=[string]$row.Cells[1].Value;$notes=[string]$row.Cells[2].Value
        if($outcome -ceq [string]$original.outcome -and $notes -ceq [string]$original.notes){continue}
        if($outcome -eq 'Passed' -and (-not $View.Confirmation.Checked -or -not $notes.Trim())){
            throw "For '$($original.title)', write what you saw and tick the personal-testing box, or leave it Not Tested."
        }
        $entries+=@{id=$id;outcome=$outcome;notes=$notes;revision=$original.revision}
    }
    return @{entries=$entries;observed=[bool]$View.Confirmation.Checked}
}

function New-080ChecklistForm($State) {
    $form=[Windows.Forms.Form]::new();$form.Text='AeroScience 0.8 — My test checklist'
    $form.Size=[Drawing.Size]::new(1100,840);$form.MinimumSize=[Drawing.Size]::new(740,540)
    $form.StartPosition='CenterScreen';$form.AutoScaleMode='Font'
    $form.Font=[Drawing.SystemFonts]::MessageBoxFont;$form.AccessibleName='AeroScience private build test checklist'
    $layout=[Windows.Forms.TableLayoutPanel]::new();$layout.Dock='Fill';$layout.ColumnCount=1;$layout.RowCount=5
    $layout.Padding=[Windows.Forms.Padding]::new(12)
    foreach($style in @('AutoSize','AutoSize','Percent','AutoSize','AutoSize')){
        [void]$layout.RowStyles.Add([Windows.Forms.RowStyle]::new([Windows.Forms.SizeType]::$style,100))
    }
    $form.Controls.Add($layout)
    $intro=[Windows.Forms.Label]::new();$intro.Dock='Fill';$intro.AutoSize=$true
    $intro.Text="Go down the list. Pick a result and write what happened. Leave things you have not tried as Not Tested.`r`nSave keeps your notes on this PC. You can close this window and return later.`r`nTesting build $($State.source_commit.Substring(0,7)) | attempt $($State.instance.Substring(0,8)). This checklist does not approve a release."
    $layout.Controls.Add($intro,0,0)
    $instructions=[Windows.Forms.TextBox]::new();$instructions.Dock='Fill';$instructions.Multiline=$true
    $instructions.ReadOnly=$true;$instructions.ScrollBars='Vertical';$instructions.Height=72
    $instructions.AccessibleName='What to check for the selected row'
    $layout.Controls.Add($instructions,0,1)
    $grid=[Windows.Forms.DataGridView]::new();$grid.Dock='Fill';$grid.AccessibleName='Checks, results and actual observations'
    $grid.AllowUserToAddRows=$false;$grid.AllowUserToDeleteRows=$false;$grid.RowHeadersVisible=$false
    $grid.AutoSizeColumnsMode='Fill';$grid.AutoSizeRowsMode='AllCells';$grid.RowTemplate.MinimumHeight=52
    $grid.MultiSelect=$false
    $check=[Windows.Forms.DataGridViewTextBoxColumn]::new();$check.HeaderText='What to check';$check.ReadOnly=$true;$check.FillWeight=34
    $result=[Windows.Forms.DataGridViewComboBoxColumn]::new();$result.HeaderText='Result';$result.FillWeight=16
    $result.Items.AddRange([object[]]@('Not Tested','Passed','Failed','Blocked'))
    $note=[Windows.Forms.DataGridViewTextBoxColumn]::new();$note.HeaderText='What I tried and what happened';$note.FillWeight=50
    $note.MaxInputLength=4000;$note.DefaultCellStyle.WrapMode='True';$check.DefaultCellStyle.WrapMode='True'
    [void]$grid.Columns.Add($check);[void]$grid.Columns.Add($result);[void]$grid.Columns.Add($note)
    $layout.Controls.Add($grid,0,2)
    $confirmation=[Windows.Forms.CheckBox]::new();$confirmation.AutoSize=$true;$confirmation.Dock='Fill'
    $confirmation.Text='I personally tried the checks I am marking Passed on this exact test build.'
    $confirmation.AccessibleName=$confirmation.Text;$layout.Controls.Add($confirmation,0,3)
    $footer=[Windows.Forms.FlowLayoutPanel]::new();$footer.Dock='Fill';$footer.AutoSize=$true;$footer.WrapContents=$true
    $save=[Windows.Forms.Button]::new();$save.Text='Save my results';$save.AutoSize=$true
    $close=[Windows.Forms.Button]::new();$close.Text='Close checklist';$close.AutoSize=$true
    $status=[Windows.Forms.Label]::new();$status.AutoSize=$true;$status.Text='Your notes stay local. No downloads, uploads or flight commands.'
    $footer.Controls.AddRange(@($save,$close,$status));$layout.Controls.Add($footer,0,4)
    $view=[pscustomobject]@{Form=$form;Grid=$grid;Confirmation=$confirmation;Save=$save;Close=$close;
        Status=$status;Instructions=$instructions;Initial=@{};State=$State;Busy=$false}
    $grid.Add_CellEnter({param($sender,$event)
        if($event.RowIndex -ge 0){$key=[string]$view.Grid.Rows[$event.RowIndex].Tag
            if($key -and $view.Initial.ContainsKey($key)){$view.Instructions.Text='What to check: '+$view.Initial[$key].instructions}}
    }.GetNewClosure())
    $grid.Add_CurrentCellDirtyStateChanged({
        if($view.Grid.IsCurrentCellDirty){[void]$view.Grid.CommitEdit([Windows.Forms.DataGridViewDataErrorContexts]::Commit)}
    }.GetNewClosure())
    $close.Add_Click({$view.Form.Close()}.GetNewClosure())
    $form.Add_FormClosing({param($sender,$event)
        if($view.Busy){$event.Cancel=$true;return}
        $request=Get-080ChecklistRequestForClose $view
        if($request -and [Windows.Forms.MessageBox]::Show('You have unsaved notes. Close without saving them?','Unsaved notes','YesNo','Question') -ne 'Yes'){$event.Cancel=$true}
    }.GetNewClosure())
    Set-080ChecklistRows $view $State
    return $view
}

function Get-080ChecklistRequestForClose($View) {
    [void]$View.Grid.EndEdit()
    foreach($row in $View.Grid.Rows){$old=$View.Initial[[string]$row.Tag]
        if([string]$row.Cells[1].Value -cne [string]$old.outcome -or [string]$row.Cells[2].Value -cne [string]$old.notes){return $true}}
    return $false
}

# Verification reads the original sealed build and its receipt. This helper and
# all observations live outside that build; no simulator/application is restarted.
Write-Host 'Checking your existing test build and loading saved notes. Nothing needs rebuilding.'
$state=Invoke-080ChecklistData 'load'
$view=New-080ChecklistForm $state
$view.Save.Add_Click({
    try {
        $request=Get-080ChecklistRequest $view
        if(-not $request.entries.Count){$view.Status.Text='No changes to save.';return}
        $view.Busy=$true;$view.Save.Enabled=$false;$view.Close.Enabled=$false;$view.Grid.Enabled=$false
        $view.Form.UseWaitCursor=$true;$view.Status.Text='Checking the build and saving your notes…'
        $saved=Invoke-080ChecklistData 'save' $request
        Set-080ChecklistRows $view $saved.state
        $view.Status.Text="Saved $($saved.saved) check(s). You can keep testing or close this window."
    } catch {
        $view.Status.Text='These changes were not all saved. Earlier notes are kept.'
        [void][Windows.Forms.MessageBox]::Show($_.Exception.Message,'Check your results','OK','Warning')
    } finally {
        $view.Busy=$false;$view.Save.Enabled=$true;$view.Close.Enabled=$true;$view.Grid.Enabled=$true;$view.Form.UseWaitCursor=$false
    }
})
Write-Host 'Opening your checklist. The existing test app and MSFS can stay open. Close the checklist when you finish.'
[void]$view.Form.ShowDialog()
$view.Form.Dispose()
