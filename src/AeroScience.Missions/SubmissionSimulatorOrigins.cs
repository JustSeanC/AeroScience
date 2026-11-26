using AeroScience.Shared;

namespace AeroScience.Missions;

// Version 3 adds an inseparable, reviewed source identity to the original bounded
// package. Version 1/2 originals retain their exact existing interpretation.
public static class SubmissionSimulatorOrigins
{
    public const int Version=1,DocumentVersion=3;
    public const string PrivacyDisclosure=SubmissionEvidenceProtocol.PrivacyDisclosure+
        " Simulator-origin metadata includes any actually reported aircraft type/registration, simulator build, adapter/mapping identity and connection epoch. Selected X-Plane samples retain original numeric dataref values/units and field-confirmation times, including recorded position/weather context. Private aircraft file paths and credentials are excluded.";
    public static bool KnownVersion(int version)=>version is 1 or SubmissionSupportingProducts.DocumentVersion or DocumentVersion;
    public static bool HasSupportingProducts(SubmissionManifest manifest)=>manifest.Items.Any(i=>i.Kind==SubmissionEvidenceKind.SupportingProductReference);
    public static bool Supported(SubmissionManifest manifest,SubmissionCapabilities? capabilities)=>capabilities is not null
        &&capabilities.OrganizationId==manifest.OrganizationId&&capabilities.PilotId==manifest.PilotId&&capabilities.Permission==OrganizationDesktopPermission.SubmitEvidence
        &&FormatSupported(manifest,capabilities);
    public static bool FormatSupported(SubmissionManifest manifest,SubmissionCapabilities capabilities)=>(manifest.Version!=DocumentVersion||capabilities.SimulatorOriginsVersion==Version)
        &&(!HasSupportingProducts(manifest)||capabilities.SupportingProductsVersion==SubmissionSupportingProducts.Version);

    public static void Validate(SubmissionDocument document)
    {
        var manifest=document.Manifest;var frozen=manifest.SimulatorOrigin;
        if((manifest.Version==DocumentVersion)!=(frozen is not null))throw new ArgumentException("Simulator-origin evidence requires document version 3. Its identity must never be stripped or downgraded.");
        if(frozen is not null)SimulatorProvenance.Validate(frozen);
        void Check(SimulatorOrigin? actual,SimulatorSampleContext? context,bool required)
        {
            if(frozen is null)
            {
                if(actual is not null||context is not null)throw new ArgumentException("Legacy evidence cannot conceal a source-qualified sample. Use the original source-aware format.");
                return;
            }
            if(actual is null){if(required)throw new ArgumentException("Source-qualified evidence is missing its original simulator identity.");return;}
            SimulatorProvenance.Validate(actual);
            if(!SimulatorProvenance.SameRecording(frozen,actual))throw new ArgumentException("The selected evidence crosses simulator identities or connection epochs.");
            if(context is not null){SimulatorProvenance.Validate(context);if(context.ConnectionEpoch!=actual.ConnectionEpoch)throw new ArgumentException("Scientific confirmation belongs to another source epoch.");}
            else if(required&&actual.SimulatorId==SimulatorPreferences.XPlane12)throw new ArgumentException("X-Plane measurements require their original field-confirmation evidence.");
        }
        foreach(var item in document.Evidence)
        {
            foreach(var sample in item.Data.Telemetry?.Samples??[])
            {Check(sample.Frame.SimulatorOrigin,sample.Frame.SimulatorContext,true);if(frozen is not null&&sample.Frame.Source!=TelemetrySource.Simulator)throw new ArgumentException("A source-qualified flight cannot include synthetic fallback.");}
            foreach(var observation in item.Data.Observations??[])
            {
                Check(observation.SimulatorOrigin,observation.SimulatorContext,observation.Provenance is ScienceProvenance.SimulatorReported||observation.Calibration is not null);
                if(observation.SimulatorOrigin?.SimulatorId==SimulatorPreferences.XPlane12&&observation.Provenance==ScienceProvenance.SimulatorReported
                    &&!SimulatorAccessPolicy.QualifiedObservation(observation))throw new ArgumentException("X-Plane observation does not match its original normalized source values or qualified timing.");
            }
            foreach(var session in item.Data.Sessions??[])
            {
                if(session.SimulatorOrigin is {} source){SimulatorProvenance.Validate(source);if(frozen is null||!SimulatorProvenance.SameRecording(frozen,source))throw new ArgumentException("Science session differs from its frozen recording source.");}
                else if(frozen is not null)throw new ArgumentException("Source-qualified science session identity is missing.");
            }
        }
    }
}
