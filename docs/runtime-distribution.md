# Native runtime distribution status

**Current status: specific permission to redistribute the MSFS 2024 native SimConnect.dll has not been established.** A public release is authorized by the project owner, but that does not grant rights in Microsoft's SDK files. Do not mark the example runtime approval record permitted just to pass packaging.

## Official evidence reviewed

- [SDK Contents](https://docs.flightsimulator.com/msfs2024/html/1_Introduction/SDK_Contents.htm) identifies the installed SDK's `Licences` folder and links its EULA.
- [Official SDK EULA](https://docs.flightsimulator.com/msfs2024/html/1_Introduction/SDK_EULA.htm), Scope of License, restricts sharing/publishing/distributing the Software, with an exception for “any distributable code, subject to the terms above” or written Microsoft approval. The public page does not identify the current native SimConnect.dll as that distributable code. Its mere presence in `SimConnect SDK\lib` is insufficient evidence.
- [SimConnect SDK](https://docs.flightsimulator.com/msfs2024/html/6_Programming_APIs/SimConnect/SimConnect_SDK.htm) documents client setup and local library use; that setup guidance does not independently grant redistribution rights.
- [Managed-client documentation](https://docs.flightsimulator.com/msfs2024/html/6_Programming_APIs/SimConnect/Programming_SimConnect_Clients_Using_Managed_Code.htm) describes a different managed wrapper/GAC setup. AeroScience uses native exports, including MSFS 2024 CommBus. Do not substitute an old FSX/MSFS 2020 MSI or managed wrapper without verifying compatibility and permission.

These sources were successfully retrieved during this release review. No current official SDK installer, installed licence notices, runtime DLL or redistribution list is available in this Linux environment. No permitted standalone graphical installer for this exact native CommBus runtime has been verified here.

## Smallest remaining permission check

On the gaming PC, inspect `C:\MSFS 2024 SDK\Licences` and any SimConnect-specific licence/REDIST notices supplied with that installed SDK. Identify a grant specifically covering distribution of the native `SimConnect.dll` with an add-on. If it is not identified, obtain clarification/permission through [official SDK Dev Support](https://devsupport.flightsimulator.com/). Do not substitute a community download or ask testers to install the development SDK.

Once permission is established, copy `docs/runtime-approval.example.json` to `C:\AeroScience-Release-Records\runtime-approval.json` (outside the source checkout). Complete:

- `redistribution_permitted`: true only after that specific grant is established.
- `permission_for_file`: `SimConnect.dll`.
- `runtime_sha256`: SHA-256 of the actual official native DLL to bundle (either letter case).
- `permission_source`: identifying official document/section, redistribution list or Microsoft permission reference.
- `permission_excerpt`: the relevant grant, including its conditions. Do not paste the generic exception alone as proof the DLL is designated distributable.
- `reviewed_by`: a maintainer name/label suitable for a public release record.
- `reviewed_utc`: an actual ISO 8601 timestamp with timezone, such as `2026-10-17T00:00:00Z` (replace with the actual review time).

That record is included in the public ZIP. Do not put credentials, private correspondence, usernames/absolute personal paths or confidential information in it. Retain supporting permission evidence locally. The record is an attestation backed by evidence, not an automatic licence determination.

The Windows candidate script checks the approved file's hash and runs the existing native x64 architecture/loading/export checker. Metadata-only inspection cannot pass this gate. The report must include all nine used telemetry/CommBus exports. Build receipts, licence approval and the exact tested payload remain separate checks.

## Supported graphical prerequisites

.NET/ASP.NET/Windows Forms assets are self-contained. No .NET SDK is needed by testers. When the native DLL requires Microsoft Visual C++ dependencies, use Microsoft's [official supported redistributable page](https://learn.microsoft.com/en-us/cpp/windows/latest-supported-vc-redist) and its x64 installer. The app's Settings button opens that page; testers download/run the official installer graphically, verify Microsoft as publisher and restart AeroScience. Validate this route on a supported Windows installation during minimum acceptance. The optional Visual C++ route does **not** install or license SimConnect itself.

If native-runtime permission cannot be established, publication stays blocked until Microsoft supplies a legitimate runtime delivery route that does not require SDK installation/manual DLL copying. Do not distribute an incomplete app as the requested complete alpha.
