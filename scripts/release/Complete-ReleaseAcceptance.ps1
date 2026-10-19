#Requires -Version 7.0
[CmdletBinding()]
param([Parameter(Mandatory)][string]$AcceptanceRecord)
$ErrorActionPreference = 'Stop'
if (-not $IsWindows) { throw 'The installed-release checklist runs on the Windows test PC.' }
Add-Type -AssemblyName System.Windows.Forms
Add-Type -AssemblyName System.Drawing
$path = (Resolve-Path -LiteralPath $AcceptanceRecord).Path
$record = Get-Content -LiteralPath $path -Raw | ConvertFrom-Json
if (-not $record.installer_sha256 -or -not $record.source_commit) { throw 'Use the installer-candidate acceptance record, not an unrelated JSON file.' }
$form = [Windows.Forms.Form]::new()
$form.Text = "AeroScience $($record.version) — Actual installed testing"
$form.Size = [Drawing.Size]::new(1100, 800)
$form.StartPosition = 'CenterScreen'
$layout = [Windows.Forms.TableLayoutPanel]::new()
$layout.Dock = 'Fill'; $layout.RowCount = 4; $layout.ColumnCount = 1
$layout.RowStyles.Add([Windows.Forms.RowStyle]::new('Absolute', 100))
$layout.RowStyles.Add([Windows.Forms.RowStyle]::new('Absolute', 40))
$layout.RowStyles.Add([Windows.Forms.RowStyle]::new('Percent', 100))
$layout.RowStyles.Add([Windows.Forms.RowStyle]::new('Absolute', 50))
$form.Controls.Add($layout)
$intro = [Windows.Forms.Label]::new(); $intro.Dock = 'Fill'
$intro.Text = "Record only checks you actually performed on this installer. Automated tests do not verify MSFS, VR or physical iPad behavior. Hover over a check for its instructions. Leave incomplete checks pending; you can save and return later.`r`nSource: $($record.source_commit)`r`nInstaller SHA-256: $($record.installer_sha256)"
$layout.Controls.Add($intro, 0, 0)
$identity = [Windows.Forms.FlowLayoutPanel]::new(); $identity.Dock = 'Fill'
$label = [Windows.Forms.Label]::new(); $label.Text = 'Tester / GitHub name:'; $label.AutoSize = $true
$tester = [Windows.Forms.TextBox]::new(); $tester.Width = 300
$tester.Text = if ($record.tester) { $record.tester } else { $env:USERNAME }
$identity.Controls.AddRange(@($label, $tester)); $layout.Controls.Add($identity, 0, 1)
$grid = [Windows.Forms.DataGridView]::new(); $grid.Dock = 'Fill'
$grid.AllowUserToAddRows = $false; $grid.AllowUserToDeleteRows = $false
$grid.RowHeadersVisible = $false; $grid.AutoSizeColumnsMode = 'Fill'; $grid.RowTemplate.Height = 50
$check = [Windows.Forms.DataGridViewTextBoxColumn]::new(); $check.Name = 'Check'; $check.ReadOnly = $true; $check.FillWeight = 28
$result = [Windows.Forms.DataGridViewComboBoxColumn]::new(); $result.Name = 'Result'; $result.FillWeight = 12
$result.Items.AddRange(@('pending', 'passed', 'failed'))
$note = [Windows.Forms.DataGridViewTextBoxColumn]::new(); $note.Name = 'Actual result / test notes'; $note.FillWeight = 60
$note.DefaultCellStyle.WrapMode = 'True'
[void]$grid.Columns.Add($check); [void]$grid.Columns.Add($result); [void]$grid.Columns.Add($note)
foreach ($property in $record.checks.PSObject.Properties) {
    $index = $grid.Rows.Add($property.Name, $property.Value.result, $property.Value.evidence)
    $grid.Rows[$index].Cells[0].ToolTipText = $property.Value.instructions
}
$layout.Controls.Add($grid, 0, 2)
$save = [Windows.Forms.Button]::new(); $save.Text = 'Save actual results'; $save.Dock = 'Right'; $save.Width = 220
$save.Add_Click({
    $grid.EndEdit()
    if (-not $tester.Text.Trim()) { [void][Windows.Forms.MessageBox]::Show('Enter your tester name.'); return }
    foreach ($row in $grid.Rows) {
        $value = [string]$row.Cells[1].Value; $evidence = ([string]$row.Cells[2].Value).Trim()
        if ($value -eq 'passed' -and -not $evidence) { [void][Windows.Forms.MessageBox]::Show("Add an actual test note for $($row.Cells[0].Value), or leave it pending."); return }
    }
    foreach ($row in $grid.Rows) {
        $entry = $record.checks.([string]$row.Cells[0].Value)
        $entry.result = [string]$row.Cells[1].Value; $entry.evidence = ([string]$row.Cells[2].Value).Trim()
    }
    $record.tester = $tester.Text.Trim(); $record.tested_utc = [DateTimeOffset]::UtcNow.ToString('o')
    $backup = $path + '.before-guided-' + [Guid]::NewGuid().ToString('N') + '.json'
    Copy-Item -LiteralPath $path -Destination $backup
    $temporary = $path + '.' + [Guid]::NewGuid().ToString('N') + '.tmp'
    $record | ConvertTo-Json -Depth 100 | Set-Content -LiteralPath $temporary -Encoding utf8
    Move-Item -LiteralPath $temporary -Destination $path -Force
    [void][Windows.Forms.MessageBox]::Show('Saved. Original results were backed up. Finalization checks all required results together and verifies this exact installer; it does not rebuild or upload it.')
    $form.Close()
})
$layout.Controls.Add($save, 0, 3)
[void]$form.ShowDialog()
