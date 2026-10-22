using System.Text.Json;
using AeroScience.Shared;

namespace AeroScience.Missions;

/// <summary>Pure operating-ui-1 mapping of typed P04 rule identities. No provider, raster or archive work.</summary>
public static class SensorOperatingProjection
{
    private sealed record Check(OperatingRuleAssessment Rule, AssessmentStatus Status, string Explanation, string? Guidance);
    public static SensorOperatingOverview Create(EngineSnapshot flight, CollectionState? collection,
        InstrumentConfiguration[] settings, EffectiveInstrumentConfiguration[] configurations,
        InstrumentOperatingReport[] reports, DateTimeOffset? contextUtc, DateTimeOffset now, bool collectionFault=false)
    {
        var session = collection?.CurrentSessionId;
        var cards = settings.Select(config =>
        {
            var effective = configurations.FirstOrDefault(c => c.Settings.InstrumentId == config.InstrumentId && Same(c.Settings, config));
            var report = reports.FirstOrDefault(r => r.Configuration.Settings.InstrumentId == config.InstrumentId &&
                r.Assessment.MissionId == flight.ActiveMission?.Id && r.Assessment.SessionId == session &&
                r.Assessment.ConfigurationId == effective?.Id && Same(r.Configuration, effective));
            var evidenceUtc = report?.Input.Frame?.TimestampUtc;
            var fresh = report is not null && Fresh(report.Assessment.AssessedUtc, now) &&
                evidenceUtc is {} time && Fresh(time, now) && report.Input.Frame is { IsValid: true, IsPaused: false } &&
                report.Input.Connection.State == ConnectionState.Connected &&
                flight.Connection.State == ConnectionState.Connected && flight.Telemetry is { IsValid: true, IsPaused: false } current && Fresh(current.TimestampUtc, now) &&
                (contextUtc is null || report.Assessment.AssessedUtc > contextUtc) &&
                report.Assessment.AssessedUtc >= effective!.EffectiveUtc;
            var checks = new List<Check>(); var unknown = new List<string>();
            var supportedVersion = report is { Policy: "hybrid-realistic-1", Assessment.Version: 1 };
            foreach (var rule in report?.Assessment.Rules ?? [])
            {
                if (rule.Rule.Revision != 1 || !Known(rule.Rule.Id))
                { supportedVersion = false; unknown.Add("An operating rule/version is unsupported by this presentation; its original evidence is retained."); continue; }
                var check = Map(rule, config);
                if (check is not null) checks.Add(check);
                else if (rule.Rule.Id == "cadence.spacing") unknown.Add("Saved sample spacing is calculated; compliance is unassessed unless the recorded cadence rule identifies a concern. Detector sampling is not measured.");
                else if (rule.Rule.Id == "planning.overlap") unknown.Add("No applicable saved overlap target was assessed.");
            }
            var unassessed = checks.Where(c => c.Status != AssessmentStatus.Supported && !Concern(c.Status)).ToArray();
            var required=new List<string>();
            if(RemoteAcquisition.Instruments.ContainsKey(config.InstrumentId))required.Add("geometry.ground-intersection");
            if(config.Payload?.Profile is {} reference)
            {
                if(reference.MinimumOperatingCelsius is not null||reference.MaximumOperatingCelsius is not null)required.Add("environment.payload-temperature");
                if(reference.Id.StartsWith("riegl.",StringComparison.Ordinal))required.Add("riegl.conditional-range");
                if(reference.Id.StartsWith("nasa.aviris-ng",StringComparison.Ordinal))required.Add("aviris-ng.nominal-line-rate");
            }
            var missing=required.Where(id=>report?.Assessment.Rules.Any(r=>r.Rule.Id==id)!=true).ToArray();
            unknown.AddRange(missing.Select(id=>"Required operating evidence is missing: "+id+". No pass is inferred."));
            if(config.Payload?.Profile.Id.StartsWith("generic.",StringComparison.Ordinal)==true)
                unknown.Add("The generic model has no documented detector or environmental operating envelope; supported geometry is not hardware compliance.");
            unknown.AddRange(unassessed.Select(c => c.Explanation));
            if (checks.Count == 0) unknown.Add("No supported physical operating envelope has been assessed; available measurements do not establish hardware compliance.");
            var caution = checks.FirstOrDefault(c => Concern(c.Status));
            var state = !config.Enabled ? SensorOperatingState.Disabled :
                flight.ActiveMission is null || collectionFault || collection?.Phase != CollectionPhase.Collecting || session is null ? SensorOperatingState.NotCollecting :
                !fresh || !supportedVersion ? SensorOperatingState.CannotAssess :
                caution is not null ? SensorOperatingState.Caution :
                checks.Count == 0 || unassessed.Length > 0 || missing.Length>0 ? SensorOperatingState.CannotAssess : SensorOperatingState.WithinCheckedLimits;
            var phase = flight.ActiveMission is null ? "No flight recording" : collectionFault ? "Collection held — resolve the session/evidence storage fault" : collection?.Phase switch
            {
                CollectionPhase.Armed => "Armed — science not started", CollectionPhase.Collecting => "Collecting",
                CollectionPhase.Paused => "Paused", CollectionPhase.BetweenSessions => "Session ended — flight recording continues",
                CollectionPhase.Stopped => "Science stopped", _ => "Collection state unknown"
            };
            var primary = state switch
            {
                SensorOperatingState.Disabled => "This instrument is disabled in the current configuration.",
                SensorOperatingState.NotCollecting => "Science is not actively collecting. Earlier favorable assessments are not current operating clearance.",
                SensorOperatingState.CannotAssess when report is null => "Waiting for an assessment of this session and effective configuration.",
                SensorOperatingState.CannotAssess when !fresh => "Telemetry or assessment evidence is stale, disconnected, paused or predates the latest collection change.",
                SensorOperatingState.CannotAssess when !supportedVersion => "The assessment policy or rule version is unsupported by this status view.",
                SensorOperatingState.CannotAssess => missing.Length>0?"Applicable operating evidence is missing; no operating pass is inferred.":unassessed.FirstOrDefault()?.Explanation ?? "No documented operating checks can establish compliance.",
                SensorOperatingState.Caution => caution!.Explanation,
                _ => "The evaluated, applicable checks are within their supported limits. Unchecked capabilities remain unknown."
            };
            var guidance = state == SensorOperatingState.Caution ? caution!.Guidance :
                state == SensorOperatingState.CannotAssess && fresh ? unassessed.FirstOrDefault()?.Guidance :
                state == SensorOperatingState.CannotAssess ? "Check the simulator connection and wait for fresh evidence. No flight maneuver can be recommended from missing evidence." : null;
            return new SensorOperatingCard(config.InstrumentId, ScienceCatalog.Definitions.Single(d => d.Id == config.InstrumentId).Name,
                config.Enabled, config.Payload is {} payload ? (payload.Custom ? "Custom · " : "") + payload.Profile.Name : "Simulator channels — no physical hardware profile",
                config.Payload?.Profile.Version, phase, session, effective, state, primary, guidance,
                state==SensorOperatingState.Caution?checks.Where(c=>Concern(c.Status)).Skip(1).Select(c=>c.Explanation+" "+c.Guidance).ToArray():[],unknown.Distinct().ToArray(),
                checks.Count(c => c.Status == AssessmentStatus.Supported || Concern(c.Status)), unassessed.Length+missing.Length,
                Data(report, fresh && supportedVersion, flight.Research?.Instruments.FirstOrDefault(i => i.InstrumentId == config.InstrumentId), config, state, now,flight.Research?.ScienceStorageError),
                report?.Assessment.AssessedUtc, evidenceUtc, fresh, report);
        }).ToArray();
        return new(1, now, flight.ActiveMission?.Id, session, collection?.Phase, cards,
            ["Operating status describes supported checks, not physical hardware health, certification or measurement accuracy.",
             "Unknown internal temperature is not a failure. Source QA is independent of aircraft/instrument operation.",
             "Historical Landsat observations are not live FLIR imagery; imported elevation is not airborne RIEGL laser returns."]);
    }
    private static bool Same<T>(T a, T b) => JsonSerializer.Serialize(a, WireJson.Options) == JsonSerializer.Serialize(b, WireJson.Options);
    private static bool Fresh(DateTimeOffset time, DateTimeOffset now) => (now - time).TotalSeconds is >= -2 and <= 6;
    private static bool Concern(AssessmentStatus status) => status is AssessmentStatus.Caution or AssessmentStatus.Invalid;
    private static bool Known(string id) => id is "geometry.ground-intersection" or "aircraft.msl" or "orientation.pitch" or "orientation.bank" or
        "orientation.mount-forward" or "orientation.mount-right" or "cadence.spacing" or "planning.forward-overlap" or "planning.side-overlap" or
        "planning.overlap" or "environment.payload-temperature" or "riegl.conditional-range" or "aviris-ng.nominal-line-rate" or "source.availability" or
        "simulator.missing" or "simulator.temperature" or "simulator.pressure" or "simulator.wind-speed" or "simulator.wind-direction" or "simulator.density" or
        "calibration.temperature" or "calibration.pressure" or "calibration.density" or "calibration.wind-speed" or "calibration.wind-direction";
    private static Check? Map(OperatingRuleAssessment rule, InstrumentConfiguration config)
    {
        var status = rule.Rule.Id=="geometry.ground-intersection" ? rule.DerivationValidity switch
            { AssessmentStatus.Invalid=>AssessmentStatus.Caution, AssessmentStatus.Supported=>rule.Compliance, _=>AssessmentStatus.Unknown } : rule.Compliance;
        var concern = Concern(status);
        return rule.Rule.Id switch
        {
            "geometry.ground-intersection" => new(rule, rule.DerivationValidity == AssessmentStatus.Invalid ? AssessmentStatus.Caution : status,
                concern ? "Aircraft/mount geometry cannot produce a defensible ground-intersecting footprint." : status == AssessmentStatus.Supported ? "Ground-intersection geometry is supported." : "Actual AGL and usable aircraft/mount evidence are needed to assess ground intersection; MSL is not a substitute.",
                "Review AGL evidence and modeled mount/orientation settings. No unsupported bank, pitch or altitude limit is imposed."),
            "riegl.conditional-range" => new(rule, status,
                concern ? "Modeled center-ray slant range is below the documented RIEGL 100 m minimum." : "RIEGL operating range cannot be fully assessed from altitude alone; reflectance, incidence, visibility and settings remain unknown.",
                concern ? "Review the modeled slant range and documented reference settings when planning. This is not measured laser range or a flight maneuver instruction." : "Consult the profile reference; no universal maximum operating altitude or range pass is inferred."),
            "planning.forward-overlap" => new(rule, status, concern ? "Travel between saved samples exceeds the modeled footprint spacing needed for the saved forward-overlap target." : "Modeled forward overlap compared with the saved target.",
                "Review planned groundspeed and collection interval against the saved overlap target. Modeled overlap is not valid source-pixel coverage."),
            "planning.side-overlap" => new(rule, status, concern ? "Saved track spacing does not meet the side-overlap target for the current modeled footprint." : "Modeled side overlap compared with saved track spacing.",
                "Review saved track spacing, planning altitude and modeled swath. Actual terrain and source masks can reduce usable coverage."),
            "cadence.spacing" when concern => new(rule, status, "Recorded acquisition intervals exceed the configured cadence; along-track gaps may be larger than intended.",
                "Review collection interval and planned groundspeed. Check acquisition delays; continuous detector sampling is not established."),
            "environment.payload-temperature" when config.Payload?.Profile is { } p && (p.MinimumOperatingCelsius is not null || p.MaximumOperatingCelsius is not null) => new(rule, status,
                "Internal sensor temperature is unavailable; the documented temperature envelope cannot be checked. This is unknown, not a sensor failure.", "No adjustment can be determined from this evidence. Simulator ambient temperature is not internal sensor temperature."),
            "aviris-ng.nominal-line-rate" => new(rule, status, concern ? "Configured nominal AVIRIS-NG line rate is outside its documented 10–100 lines/s range." : "Configured AVIRIS-NG nominal line rate checked against its reference range.",
                "Review the selected reference/custom line rate. It models configuration, not observed detector operation."),
            _ => null
        };
    }
    private static SensorScientificDataStatus Data(InstrumentOperatingReport? report, bool fresh, InstrumentState? instrument,
        InstrumentConfiguration config, SensorOperatingState operating, DateTimeOffset now,string? storageError)
    {
        if(storageError is not null)return new(SensorScientificDataState.Unavailable,"The selected Scientific Library is unavailable. Current source acquisition/storage cannot be confirmed; this is not an aircraft sensor failure.",storageError);
        var remote = RemoteAcquisition.Instruments.ContainsKey(config.InstrumentId);
        if(!remote)fresh=fresh&&(report?.Input.Atmosphere??report?.Input.Frame?.Atmosphere) is {} atmosphere&&Fresh(atmosphere.TimestampUtc,now);
        var rules = report?.Assessment.Rules.Where(r => remote ? r.Rule.Id == "source.availability" : r.Rule.Id.StartsWith("simulator.", StringComparison.Ordinal)).ToArray() ?? [];
        var qa = rules.Select(r => r.SourceQuality).ToArray();
        var state = !fresh || qa.Length == 0 ? SensorScientificDataState.Unknown :
            qa.All(q => q == AssessmentStatus.Supported) ? SensorScientificDataState.Ready :
            qa.Any(q => q is AssessmentStatus.Supported or AssessmentStatus.Caution) ? SensorScientificDataState.PartiallyAvailable :
            qa.All(q => q is AssessmentStatus.Invalid or AssessmentStatus.Unavailable or AssessmentStatus.Missing) ? SensorScientificDataState.Unavailable :
            instrument?.Quality == ObservationQuality.Waiting ? SensorScientificDataState.Pending : SensorScientificDataState.Unknown;
        if (state == SensorScientificDataState.Unknown && operating is not (SensorOperatingState.Disabled or SensorOperatingState.NotCollecting) && instrument?.Quality == ObservationQuality.Waiting)
            state = SensorScientificDataState.Pending;
        var explanation = remote ? state switch
        {
            SensorScientificDataState.Ready => "Recent compatible external-source acquisition has valid QA at its sampled support; a local dataset or scene outline does not guarantee valid measurements everywhere.",
            SensorScientificDataState.PartiallyAvailable => "Source evidence is mixed or limited. Only supported measurements may be used; unavailable pixels remain unavailable.",
            SensorScientificDataState.Unavailable => "The assessed external-source acquisition is unavailable or invalid. This is not an aircraft sensor malfunction.",
            SensorScientificDataState.Pending => "Waiting for source acquisition/preparation evidence; a successful result is not guaranteed.",
            _ => "No sufficiently fresh source-quality evidence for this session/configuration. Availability is unknown, not a hardware fault."
        } : state switch
        {
            SensorScientificDataState.Ready => "Original simulator channels are available. This does not certify physical sensor operation or prove a saved acquisition.",
            SensorScientificDataState.PartiallyAvailable => "Some simulator channels are available; missing/unassessed channels are not valid measurements.",
            SensorScientificDataState.Unavailable => "The assessed simulator channels are unavailable; no values are inferred.",
            _ => "Current simulator-channel availability is not yet established. No external satellite dataset is required."
        };
        var context=config.InstrumentId switch
        {
            "thermal-imager"=>" Landsat surface temperatures are historical external observations; a FLIR reference profile does not create a live camera image.",
            "terrain-scanner"=>" Imported DEM/DSM elevations are external data, not airborne RIEGL laser returns or a measured point cloud.",
            "multispectral-imager" or "hyperspectral-imager"=>" External imagery retains its original observation date; sampling during a simulator flight does not turn it into airborne camera measurements.",
            _=>""
        };
        return new(state, explanation+context, instrument?.Message);
    }
}
