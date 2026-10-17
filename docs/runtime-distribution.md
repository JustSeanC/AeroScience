# Native runtime distribution status and private evidence

**Current status: the project maintainer confirms that redistribution of the native MSFS 2024 SimConnect.dll with AeroScience is authorized, with no additional distribution conditions reported.** This supersedes the earlier unresolved-permission status. The original authorization correspondence remains private and is not required in source control, release assets, logs or feedback reports.

The remaining work is technical: bind that confirmed authorization to the actual SDK DLL's SHA-256, validate its native Windows architecture/loading/exports, build the genuine Community package and complete minimum real Windows/MSFS acceptance. This Linux environment has no access to the gaming PC's SDK file, so its hash/version cannot be invented or filled in here. No further identity or correspondence details are needed for the public attestation.

## Create the public attestation on the gaming PC

In the maintainer Git checkout, run:

```powershell
pwsh -NoProfile -ExecutionPolicy RemoteSigned -File .\scripts\release\New-RuntimeApproval.ps1 -RedistributionAuthorized -ConditionsSummary 'No additional distribution conditions reported by the project maintainer.'
```

Defaults:

- Official runtime: `C:\MSFS 2024 SDK\SimConnect SDK\lib\SimConnect.dll`.
- Approval record: `C:\AeroScience-Release-Records\runtime-approval.json` (outside the checkout).
- Public reviewer label: `AeroScience maintainer`; no personal name/email is required.

The helper reads only the specified DLL, calculates its actual SHA-256, records the current UTC attestation timestamp and saves a version 2 public record. The timestamp records when the attestation was created, not the date of private correspondence. It refuses to replace an existing record. If an earlier record exists, retain it and choose a new `-OutputPath`, then pass that path to the candidate builder. Use `-RuntimePath` only if the official SDK is installed elsewhere.

The public record includes the authorization summary, confirmed conditions, DLL hash, reviewer label and record timestamp. It uses a generic source: “Project maintainer confirmation; supporting authorization retained privately.” It contains no quoted grant, sender details, correspondence filenames or private evidence reference. The generic example JSON remains unapproved/blank so it cannot accidentally authorize an unrelated future file; use the helper for this confirmed release.

The helper records authorization and hash only. `Build-ReleaseCandidate.ps1` separately verifies the actual Windows DLL loading and all nine telemetry/CommBus exports, plus the official SDK ABI/package build. Creating an approval record is not a hardware/runtime test or release acceptance pass.

## Keep original evidence private

Retain the original correspondence separately from the checkout, published companion folder and candidate folder. It need not be uploaded to this chat or GitHub. Do not paste it into a public summary field. The packager uses a strict public-field projection, omits local `permission_excerpt`, private references and extra fields, and replaces the source field with the generic attestation label. Final validation refuses non-public approval fields if reintroduced. Email archives (`.eml`, `.msg`, `.mbox`, `.pst`, `.ost`) are refused in distribution inputs.

Only a non-confidential summary of any required notices/attribution/version restrictions would belong in `conditions_summary`. The maintainer has reported none for this authorization. If scope or conditions change later, create a new record for the appropriate DLL and include only notices specifically intended for public distribution. Never publish the underlying correspondence to satisfy a packaging check.

## Official references and technical prerequisites

The previous review consulted [SDK Contents](https://docs.flightsimulator.com/msfs2024/html/1_Introduction/SDK_Contents.htm), the [public SDK EULA](https://docs.flightsimulator.com/msfs2024/html/1_Introduction/SDK_EULA.htm), [SimConnect SDK](https://docs.flightsimulator.com/msfs2024/html/6_Programming_APIs/SimConnect/SimConnect_SDK.htm) and [managed-client guidance](https://docs.flightsimulator.com/msfs2024/html/6_Programming_APIs/SimConnect/Programming_SimConnect_Clients_Using_Managed_Code.htm). The current permission status relies on the maintainer's confirmed authorization, not on inferring redistribution rights from those generic pages or the DLL's presence.

Use the official native MSFS 2024 DLL with CommBus exports; an old FSX/MSFS 2020 installer or the managed wrapper is not a substitute. .NET/ASP.NET/Windows Forms assets are self-contained, so testers need no development SDK. When Microsoft Visual C++ dependencies are required, the app's Settings button opens Microsoft's [official redistributable page](https://learn.microsoft.com/en-us/cpp/windows/latest-supported-vc-redist); install the x64 redistributable graphically and restart AeroScience. Validate that end-user route during Windows acceptance.

The first release remains a local gaming-PC build and manual GitHub Releases web upload. No self-hosted runner or private-destination token is required. Follow [the Windows release guide](public-release-windows.md).
