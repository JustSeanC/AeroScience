#Requires -Version 7.0
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$RuntimePath,
    [string]$ReportPath,
    [switch]$MetadataOnly
)
$ErrorActionPreference = 'Stop'
$path = (Resolve-Path -LiteralPath $RuntimePath).Path
$bytes = [IO.File]::ReadAllBytes($path)
if ($bytes.Length -lt 64 -or $bytes[0] -ne 0x4D -or $bytes[1] -ne 0x5A) { throw 'Not a Windows PE image.' }
$offset = [BitConverter]::ToUInt32($bytes, 60)
if ([uint64]$offset + 26 -gt $bytes.Length -or [BitConverter]::ToUInt32($bytes, $offset) -ne 0x4550) { throw 'Invalid PE header.' }
$machine = [BitConverter]::ToUInt16($bytes, $offset + 4)
$characteristics = [BitConverter]::ToUInt16($bytes, $offset + 22)
if ($machine -ne 0x8664 -or ($characteristics -band 0x2000) -eq 0) { throw 'Expected a native Windows x64 DLL, not an EXE/x86 image.' }
# Reject the mixed-mode managed wrapper: the CLI header is directory entry 14 in PE32+.
$optionalSize = [BitConverter]::ToUInt16($bytes, $offset + 20)
if ($optionalSize -lt 232 -or [uint64]$offset + 24 + $optionalSize -gt $bytes.Length -or [BitConverter]::ToUInt16($bytes, $offset + 24) -ne 0x20B) {
    throw 'Invalid/truncated x64 optional header.'
}
$cliRva = [BitConverter]::ToUInt32($bytes, $offset + 24 + 112 + 14 * 8)
if ($cliRva -ne 0) { throw 'This DLL has a managed CLR header. Use the native SimConnect.dll, not Microsoft.FlightSimulator.SimConnect.dll.' }
$version = [Diagnostics.FileVersionInfo]::GetVersionInfo($path)
$report = [ordered]@{
    checked_utc = [DateTime]::UtcNow.ToString('o')
    runtime_file = $path
    sha256 = (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToLowerInvariant()
    file_version = $version.FileVersion
    architecture = 'Windows x64'
    export_check = 'not run'
    simulator_connection = 'not tested'
}
if (-not $MetadataOnly) {
    if (-not $IsWindows -or [Runtime.InteropServices.RuntimeInformation]::ProcessArchitecture -ne [Runtime.InteropServices.Architecture]::X64) {
        throw 'Runtime loading/export checks require PowerShell 7 x64 on Windows. MetadataOnly does not verify loading or SDK exports.'
    }
    $handle = [IntPtr]::Zero
    try {
        try { $handle = [Runtime.InteropServices.NativeLibrary]::Load($path) }
        catch { throw "Windows could not load the DLL or a dependency. Check its official SDK origin and required Microsoft Visual C++ x64 runtime. Loader detail: $($_.Exception.Message)" }
        $exports = @('SimConnect_Open', 'SimConnect_Close', 'SimConnect_CallDispatch', 'SimConnect_AddToDataDefinition',
            'SimConnect_RequestDataOnSimObject', 'SimConnect_SubscribeToSystemEvent', 'SimConnect_RequestSystemState',
            'SimConnect_SubscribeToCommBusEvent', 'SimConnect_CallCommBusEvent', 'SimConnect_GetLastSentPacketID')
        foreach ($name in $exports) {
            $address = [IntPtr]::Zero
            if (-not [Runtime.InteropServices.NativeLibrary]::TryGetExport($handle, $name, [ref]$address)) {
                throw "Required SDK export is missing: $name. Use the current MSFS 2024 native runtime with CommBus support."
            }
        }
        $report.export_check = 'passed'
        $report['exports'] = $exports
    } finally {
        if ($handle -ne [IntPtr]::Zero) { [Runtime.InteropServices.NativeLibrary]::Free($handle) }
    }
}
if ($ReportPath) {
    $fullReport = [IO.Path]::GetFullPath($ReportPath)
    if (Test-Path -LiteralPath $fullReport) { throw "Refusing to overwrite an existing runtime report: $fullReport" }
    [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($fullReport)) | Out-Null
    $report | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath $fullReport -Encoding utf8
}
[pscustomobject]$report
