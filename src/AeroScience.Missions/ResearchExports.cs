using System.Globalization;
using System.Text;
using System.Xml;
using AeroScience.Shared;

namespace AeroScience.Missions;

public static class ResearchExports
{
    public static readonly IReadOnlyDictionary<string, string> TelemetryUnits = new Dictionary<string, string>
    {
        ["timestampUtc"] = "UTC ISO 8601; host packet receipt",
        ["latitude"] = "degrees north / WGS84",
        ["longitude"] = "degrees east / WGS84",
        ["altitudeMslFeet"] = "ft above simulator mean sea level",
        ["altitudeAglFeet"] = "ft above simulator ground",
        ["indicatedAirspeedKnots"] = "kt",
        ["trueAirspeedKnots"] = "kt",
        ["groundspeedKnots"] = "kt",
        ["machNumber"] = "mach",
        ["headingDegrees"] = "degrees true",
        ["groundTrackDegrees"] = "degrees true",
        ["verticalSpeedFeetPerMinute"] = "ft/min",
        ["pitchDegrees"] = "degrees nose up",
        ["bankDegrees"] = "degrees right bank"
    };
    private static string N(double? number) => number?.ToString("R", CultureInfo.InvariantCulture) ?? "";
    private static string Q(string? text)
    {
        text ??= "";
        // Quoting alone does not prevent spreadsheet formula evaluation of user notes/names.
        if (text.Length > 0 && text[0] is '=' or '+' or '-' or '@' or '\t' or '\r') text = "'" + text;
        return "\"" + text.Replace("\"", "\"\"") + "\"";
    }
    public static void RequireCompleted(MissionStore store, Guid id)
    {
        var detail = store.Detail(id) ?? throw new ArgumentException("Mission not found.");
        if (detail.Mission.Status == MissionStatus.Recording) throw new InvalidOperationException("End the mission before exporting.");
    }
    public static async Task CsvAsync(MissionStore store, Guid id, string kind, Stream stream, CancellationToken token)
    {
        RequireCompleted(store, id);
        await using var writer = new StreamWriter(stream, new UTF8Encoding(false), 16384, leaveOpen: true);
        async Task Line(params string[] cells) { token.ThrowIfCancellationRequested(); await writer.WriteLineAsync(string.Join(',', cells).AsMemory(), token); }
        if (kind == "flight")
        {
            await Line("timestamp_utc", "source", "aircraft", "latitude_deg_n", "longitude_deg_e", "altitude_msl_ft", "altitude_agl_ft",
                "ias_kt", "tas_kt", "groundspeed_kt", "heading_true_deg", "track_true_deg", "pitch_deg", "bank_deg", "vertical_speed_ft_min", "mach",
                "ap_status", "lateral_mode", "lateral_target", "lateral_unit", "vertical_mode", "vertical_target", "vertical_unit", "speed_mode", "speed_target", "speed_unit");
            foreach (var sample in store.StreamSamples(id))
                await Line(Q(sample.TimestampUtc.ToUniversalTime().ToString("O")), Q(sample.Source.ToString()), Q(sample.Aircraft), N(sample.Latitude), N(sample.Longitude), N(sample.AltitudeMslFeet), N(sample.AltitudeAglFeet),
                    N(sample.IndicatedAirspeedKnots), N(sample.TrueAirspeedKnots), N(sample.GroundspeedKnots), N(sample.HeadingDegrees), N(sample.GroundTrackDegrees), N(sample.PitchDegrees), N(sample.BankDegrees), N(sample.VerticalSpeedFeetPerMinute), N(sample.MachNumber),
                    Q(sample.Guidance?.Autopilot.ToString()), Q(sample.Guidance?.Lateral.Mode), N(sample.Guidance?.Lateral.Target), Q(sample.Guidance?.Lateral.Unit),
                    Q(sample.Guidance?.Vertical.Mode), N(sample.Guidance?.Vertical.Target), Q(sample.Guidance?.Vertical.Unit), Q(sample.Guidance?.Speed.Mode), N(sample.Guidance?.Speed.Target), Q(sample.Guidance?.Speed.Unit));
        }
        else if (kind is "science" or "atmosphere")
        {
            var science = store.ScienceLibrary ?? throw new InvalidOperationException("Science Library is not configured in this host.");
            if (science.AvailabilityError() is { } error) throw new InvalidOperationException(error);
            await Line("mission_id", "dataset_id", "instrument_id", "timestamp_utc", "measurement", "value", "unit", "provenance", "source", "quality", "latitude_deg_n", "longitude_deg_e", "altitude_msl_ft", "position_timestamp_utc");
            foreach (var item in science.StreamObservations(id))
                await Line(Q(item.MissionId.ToString()), Q(item.DatasetId.ToString()), Q(item.InstrumentId), Q(item.TimestampUtc.ToString("O")), Q(item.MeasurementId), N(item.Value), Q(item.Unit), Q(item.Provenance.ToString()), Q(item.Source), Q(item.Quality.ToString()), N(item.Latitude), N(item.Longitude), N(item.AltitudeMslFeet), Q(item.PositionTimestampUtc?.ToString("O")));
        }
        else if (kind == "events")
        {
            await Line("event_id", "timestamp_utc", "kind", "message");
            foreach (var item in store.StreamEvents(id)) await Line(item.Id.ToString(CultureInfo.InvariantCulture), Q(item.TimestampUtc.ToString("O")), Q(item.Kind), Q(item.Message));
        }
        else throw new ArgumentException("Choose flight, science or events CSV.");
        await writer.FlushAsync(token);
    }
    public static async Task GpxAsync(MissionStore store, Guid id, Stream output, CancellationToken token)
    {
        RequireCompleted(store, id); const string ns = "http://www.topografix.com/GPX/1/1";
        await using var writer = XmlWriter.Create(output, new XmlWriterSettings { Async = true, Encoding = new UTF8Encoding(false), CloseOutput = false });
        await writer.WriteStartDocumentAsync(); await writer.WriteStartElementAsync(null, "gpx", ns);
        await writer.WriteAttributeStringAsync(null, "version", null, "1.1"); await writer.WriteAttributeStringAsync(null, "creator", null, "AeroScience");
        await writer.WriteStartElementAsync(null, "metadata", ns); await writer.WriteElementStringAsync(null, "desc", ns, "Actual simulator track; WGS84 coordinates, elevation in metres above simulator MSL, original UTC receipt timestamps."); await writer.WriteEndElementAsync();
        await writer.WriteStartElementAsync(null, "trk", ns); await writer.WriteElementStringAsync(null, "name", ns, store.Detail(id)!.Mission.Name);
        await writer.WriteStartElementAsync(null, "trkseg", ns); DateTimeOffset? last = null; var count = 0;
        foreach (var sample in store.StreamSamples(id))
        {
            token.ThrowIfCancellationRequested();
            if (last is not null && sample.TimestampUtc - last > TimeSpan.FromSeconds(65))
            { await writer.WriteEndElementAsync(); await writer.WriteStartElementAsync(null, "trkseg", ns); }
            await writer.WriteStartElementAsync(null, "trkpt", ns);
            await writer.WriteAttributeStringAsync(null, "lat", null, N(sample.Latitude)); await writer.WriteAttributeStringAsync(null, "lon", null, N(sample.Longitude));
            await writer.WriteElementStringAsync(null, "ele", ns, N(sample.AltitudeMslFeet * .3048));
            await writer.WriteElementStringAsync(null, "time", ns, sample.TimestampUtc.ToUniversalTime().ToString("O"));
            await writer.WriteEndElementAsync(); last = sample.TimestampUtc;
            if (++count % 256 == 0) await writer.FlushAsync();
        }
        await writer.WriteEndElementAsync(); await writer.WriteEndElementAsync(); await writer.WriteEndElementAsync(); await writer.WriteEndDocumentAsync(); await writer.FlushAsync();
    }
    public static async Task KmlAsync(MissionStore store, Guid id, Stream output, CancellationToken token)
    {
        RequireCompleted(store, id); const string ns = "http://www.opengis.net/kml/2.2";
        await using var writer = XmlWriter.Create(output, new XmlWriterSettings { Async = true, Encoding = new UTF8Encoding(false), CloseOutput = false });
        await writer.WriteStartDocumentAsync(); await writer.WriteStartElementAsync(null, "kml", ns); await writer.WriteStartElementAsync(null, "Document", ns);
        await writer.WriteElementStringAsync(null, "name", ns, store.Detail(id)!.Mission.Name);
        await writer.WriteStartElementAsync(null, "Placemark", ns); await writer.WriteElementStringAsync(null, "name", ns, "Recorded simulator flight path");
        await writer.WriteStartElementAsync(null, "LineString", ns); await writer.WriteElementStringAsync(null, "altitudeMode", ns, "absolute");
        await writer.WriteStartElementAsync(null, "coordinates", ns); var count = 0;
        foreach (var sample in store.StreamSamples(id))
        { token.ThrowIfCancellationRequested(); await writer.WriteStringAsync($"{N(sample.Longitude)},{N(sample.Latitude)},{N(sample.AltitudeMslFeet * .3048)}\n"); if (++count % 256 == 0) await writer.FlushAsync(); }
        await writer.WriteEndElementAsync(); await writer.WriteEndElementAsync(); await writer.WriteEndElementAsync();
        async Task Point(string name, double latitude, double longitude, double? altitude, string? note)
        {
            await writer.WriteStartElementAsync(null, "Placemark", ns); await writer.WriteElementStringAsync(null, "name", ns, name);
            if (note is not null) await writer.WriteElementStringAsync(null, "description", ns, note);
            await writer.WriteStartElementAsync(null, "Point", ns); await writer.WriteElementStringAsync(null, "altitudeMode", ns, altitude is null ? "clampToGround" : "absolute");
            await writer.WriteElementStringAsync(null, "coordinates", ns, N(longitude) + "," + N(latitude) + (altitude is { } feet ? "," + N(feet * .3048) : ""));
            await writer.WriteEndElementAsync(); await writer.WriteEndElementAsync();
        }
        var plan = store.Research(id).PlanSnapshot;
        foreach (var waypoint in plan?.Waypoints ?? []) await Point(waypoint.Name, waypoint.Latitude, waypoint.Longitude, waypoint.TargetAltitudeFeet, waypoint.Instructions);
        foreach (var marker in store.StreamMarkers(id))
            if (marker.Latitude is { } latitude && marker.Longitude is { } longitude) await Point(marker.Category, latitude, longitude, marker.AltitudeMslFeet, marker.Notes);
        if (plan?.Waypoints is { Length: > 1 } route)
        {
            await writer.WriteStartElementAsync(null, "Placemark", ns); await writer.WriteElementStringAsync(null, "name", ns, "Planned route (horizontal overlay)");
            await writer.WriteStartElementAsync(null, "LineString", ns); await writer.WriteElementStringAsync(null, "altitudeMode", ns, "clampToGround");
            await writer.WriteElementStringAsync(null, "coordinates", ns, string.Join(' ', route.Select(p => N(p.Longitude) + "," + N(p.Latitude))));
            await writer.WriteEndElementAsync(); await writer.WriteEndElementAsync();
        }
        foreach (var area in plan?.SurveyAreas ?? [])
        {
            await writer.WriteStartElementAsync(null, "Placemark", ns); await writer.WriteElementStringAsync(null, "name", ns, area.Name);
            await writer.WriteStartElementAsync(null, "Polygon", ns); await writer.WriteStartElementAsync(null, "outerBoundaryIs", ns); await writer.WriteStartElementAsync(null, "LinearRing", ns);
            await writer.WriteElementStringAsync(null, "coordinates", ns, string.Join(' ', area.Vertices.Append(area.Vertices[0]).Select(p => N(p.Longitude) + "," + N(p.Latitude))));
            await writer.WriteEndElementAsync(); await writer.WriteEndElementAsync(); await writer.WriteEndElementAsync(); await writer.WriteEndElementAsync();
        }
        await writer.WriteEndElementAsync(); await writer.WriteEndElementAsync(); await writer.WriteEndDocumentAsync(); await writer.FlushAsync();
    }
    public static async Task ReportAsync(MissionStore store, Guid id, Stream stream, CancellationToken token)
    {
        RequireCompleted(store, id);
        await using var writer = new StreamWriter(stream, new UTF8Encoding(false), 16384, leaveOpen: true);
        await writer.WriteLineAsync(Report(store, id, false).AsMemory(), token);
        await writer.WriteLineAsync("Complete research notes:");
        foreach (var note in store.StreamNotes(id)) { token.ThrowIfCancellationRequested(); await writer.WriteLineAsync($"{note.TimestampUtc:O} [{note.Phase}] {note.Text}".AsMemory(), token); }
        await writer.WriteLineAsync("Complete observation markers:");
        foreach (var marker in store.StreamMarkers(id)) { token.ThrowIfCancellationRequested(); await writer.WriteLineAsync($"{marker.TimestampUtc:O} [{marker.Category}] {marker.Notes}".AsMemory(), token); }
        await writer.WriteLineAsync("Science collection pause intervals (flight recording is independent):");
        foreach (var pause in store.SciencePauses(id)) await writer.WriteLineAsync($"{pause.StartedUtc:O} to {pause.EndedUtc:O}; seconds={pause.Seconds}; interrupted={pause.Interrupted}");
        var plan = store.Research(id).PlanSnapshot;
        if (plan is not null)
        {
            var analysis = MissionAnalysis.Compare(plan, store.StreamSamples(id), store.GetAnalysisSettings(id));
            await writer.WriteLineAsync($"Planned distance (m): {N(analysis.PlannedDistanceMetres)}; recorded connected distance (m): {N(analysis.RecordedDistanceMetres)}; excluded gaps: {analysis.ExcludedGaps}");
            foreach (var waypoint in analysis.Waypoints) await writer.WriteLineAsync($"Waypoint {waypoint.Index + 1} {waypoint.Name}: reached-in-order={waypoint.Reached}; closest (m)={N(waypoint.ClosestDistanceMetres)}; observed UTC={waypoint.ReachedUtc:O}; altitude error (ft)={N(waypoint.AltitudeErrorFeet)}");
            foreach (var limitation in analysis.Limitations) await writer.WriteLineAsync(limitation);
        }
        await writer.FlushAsync(token);
    }
    public static string Report(MissionStore store, Guid id, bool includeDetails = true)
    {
        var detail = store.Detail(id) ?? throw new ArgumentException("Mission not found."); var research = store.Research(id); var stats = store.Statistics(id);
        var text = new StringBuilder(); text.AppendLine("AeroScience — Research mission summary"); text.AppendLine(detail.Mission.Name);
        text.AppendLine($"Mission: {id}\nTemplate: {research.Template}\nMode: {detail.Mission.Mode}\nAircraft: {detail.Mission.Aircraft ?? "Unavailable"}\nSource: {detail.Mission.Source?.ToString() ?? "Unavailable"}");
        text.AppendLine($"UTC start: {detail.Mission.StartedUtc:O}\nUTC end: {detail.Mission.EndedUtc:O}\nObjective: {research.Objective ?? "Not supplied"}\nSamples: {stats.Samples}\nReported gap events: {stats.GapEvents}");
        text.AppendLine($"Altitude range (ft MSL): {N(stats.MinimumAltitudeFeet)} – {N(stats.MaximumAltitudeFeet)}\nIAS range (kt): {N(stats.MinimumIasKnots)} – {N(stats.MaximumIasKnots)}\nMach range: {N(stats.MinimumMach)} – {N(stats.MaximumMach)} ({stats.MachSamples} available samples)\nAverage received recording interval (s): {N(stats.ActualAverageIntervalSeconds)}");
        text.AppendLine("Scientific data: simulator-reported atmosphere and, where present, historical external remote-sensing datasets sampled along approximate simulator-derived footprints. Source dates are not flight times.");
        if (store.ScienceLibrary is null || store.ScienceLibrary.AvailabilityError() is { }) text.AppendLine("Scientific library unavailable in this host; no observations or findings were invented.");
        else foreach (var measurement in store.ScienceLibrary.Summary(id)) text.AppendLine($"{measurement.MeasurementId}: n={measurement.Count}, min={N(measurement.Minimum)}, max={N(measurement.Maximum)}, mean={N(measurement.Mean)} {measurement.Unit}; {measurement.FirstUtc:O} to {measurement.LastUtc:O}");
        if (store.ScienceLibrary is { } library && library.AvailabilityError() is null)
            foreach (var product in library.AllProducts(id)) text.AppendLine($"Remote product {product.Id}: {product.InstrumentId}; source={product.SourceId}; flight-packet UTC={product.FlightPacketUtc:O}; original observation={product.SourceObservationUtc:O}; processing={product.ProcessedUtc:O}; bytes={product.Bytes}; SHA-256={product.Sha256}; {product.Provenance}; quality={product.Quality}; footprint center={product.Footprint.Center.Latitude:R},{product.Footprint.Center.Longitude:R}. No scientific conclusion or objective pass/fail is asserted.");
        text.AppendLine("Research notes:"); if (research.PlanSnapshot?.PreflightNotes is { } preflight) text.AppendLine("Preflight plan: " + preflight);
        foreach (var note in includeDetails ? store.StreamNotes(id).Take(200) : []) text.AppendLine($"{note.TimestampUtc:O} [{note.Phase}] {note.Text}");
        text.AppendLine("Observation markers:"); foreach (var marker in includeDetails ? store.StreamMarkers(id).Take(200) : []) text.AppendLine($"{marker.TimestampUtc:O} [{marker.Category}] {marker.Notes}");
        text.AppendLine("Summary statistics are reproducible from saved records. No interpretation, causal finding or language-model conclusion is generated."); return text.ToString();
    }
}
