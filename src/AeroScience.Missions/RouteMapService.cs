using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AeroScience.Shared;

namespace AeroScience.Missions;

public sealed record RouteMapItem(string Kind, int Index, string Id, string Name, double Latitude, double Longitude,
    double? TargetAltitudeFeet = null, string? Instructions = null, Guid? PatternId = null, int? Area = null);
public sealed record RouteMapAircraft(double Latitude, double Longitude, double HeadingDegrees, DateTimeOffset TimestampUtc,double? GroundTrackDegrees=null,double? GroundspeedKnots=null);
public sealed record RouteMapState(string Revision, string Name, Guid? MissionId, int Items, int Waypoints, int NextWaypoint,
    RouteMapAircraft? Aircraft, TrackPoint[] Track, long NextSequence, bool HasMore, bool Downsampled, MissionMarker[] Markers, string ProgressExplanation, int? SelectedWaypoint = null, double? DistanceMetres = null, double? BearingDegrees = null);
public sealed record RouteMapChunk(string Revision, int Offset, RouteMapItem[] Items, int NextOffset, bool Complete);

// Immutable plan snapshot geometry is cached; live messages contain only bounded updates.
public sealed class RouteMapService(MissionCoordinator coordinator)
{
    private readonly object gate = new();
    private MissionPlan? plan;
    private string key = "", revision = "none";
    private RouteMapItem[] items = [];
    private int nextWaypoint;
    private DateTimeOffset? observed;
    private DateTimeOffset loaded;
    private static string Short(string? s, int limit) => s is null ? "" : s.Length <= limit ? s : s[..limit] + "…";
    private void Load(Guid? selected)
    {
        var state = coordinator.Snapshot(); var mission = state.ActiveMission?.Id;
        var newKey = mission?.ToString() ?? selected?.ToString() ?? "none";
        if (newKey == key && (mission is not null || DateTimeOffset.UtcNow - loaded < TimeSpan.FromSeconds(15))) return;
        var next = mission is { } id ? coordinator.Store.Research(id).PlanSnapshot : selected is { } p ? coordinator.Store.Plan(p) : null;
        var digest = next is null ? (mission is null ? "none" : "flight-" + mission) : Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(newKey + JsonSerializer.Serialize(next, WireJson.Options)))).ToLowerInvariant();
        loaded = DateTimeOffset.UtcNow; key = newKey;
        if (digest == revision) return;
        plan = next; revision = digest; nextWaypoint = 0; observed = null;
        var geometry = new List<RouteMapItem>();
        foreach (var (w, i) in (plan?.Waypoints ?? []).Select((w, i) => (w, i)))
            geometry.Add(new("waypoint", i, w.Id?.ToString() ?? $"{plan!.Id:N}:{i}", Short(w.Name, 60), w.Latitude, w.Longitude, w.TargetAltitudeFeet, Short(w.Instructions, 120), w.PatternId));
        foreach (var (area, i) in (plan?.SurveyAreas ?? []).Select((a, i) => (a, i)))
            foreach (var (v, j) in area.Vertices.Select((v, j) => (v, j))) geometry.Add(new("survey", j, $"{plan!.Id:N}:area:{i}:{j}", Short(area.Name, 60), v.Latitude, v.Longitude, Area: i));
        items = geometry.ToArray();
    }
    public RouteMapState State(Guid? selected, long? after, bool includeTrack = true, int? waypointIndex = null)
    {
        lock (gate)
        {
            Load(selected); var snapshot = coordinator.Snapshot(); var mission = snapshot.ActiveMission?.Id;
            var frame = snapshot.Telemetry;
            var fresh = snapshot.Connection.State == ConnectionState.Connected && frame is not null && (DateTimeOffset.UtcNow - frame.TimestampUtc).TotalSeconds is >= -2 and < 6;
            var route = plan?.Waypoints ?? [];
            if (fresh && frame!.TimestampUtc != observed)
            { observed = frame.TimestampUtc; nextWaypoint = MissionAnalysis.AdvanceWaypoint(route, nextWaypoint, new(frame.Latitude, frame.Longitude), 1000); }
            var track = includeTrack && mission is { } id ? coordinator.Store.Track(id, 24, after, recent: true) : new TrackPage([], 0, false, 0, false);
            // Notes/scientific context are intentionally excluded from this small map overlay.
            var markers = mission is { } m ? coordinator.Store.Markers(m, 4).Select(v => v with { Notes = null, ScientificContextJson = null, Category = Short(v.Category, 30) }).ToArray() : [];
            var selectedIndex = waypointIndex ?? Math.Min(nextWaypoint, route.Length - 1);
            if (waypointIndex is < 0 || route.Length > 0 && selectedIndex >= route.Length) throw new ArgumentException("Selected waypoint is outside the current route.");
            var target = fresh && selectedIndex >= 0 && selectedIndex < route.Length ? Geodesy.Point(route[selectedIndex]) : null;
            var position = fresh ? new Coordinates(frame!.Latitude, frame.Longitude) : null;
            return new(revision, Short(plan?.Name, 80), mission, items.Length, route.Length, nextWaypoint,
                fresh ? new(frame!.Latitude, frame.Longitude, frame.HeadingDegrees, frame.TimestampUtc,frame.GroundTrackDegrees,frame.GroundspeedKnots) : null,
                track.Points, track.NextSequence, track.HasMore, track.Downsampled, markers,
                "Next waypoint uses observed positions in route order within 1,000 m while this map is open. It is approximate, may miss crossings and is not a completed-flight analysis or aircraft guidance command.",
                selectedIndex >= 0 ? selectedIndex : null, target is not null ? Geodesy.Distance(position!, target) : null, target is not null ? (Geodesy.Bearing(position!, target) + 360) % 360 : null);
        }
    }
    public RouteMapChunk Chunk(Guid? selected, string expectedRevision, int offset)
    {
        lock (gate)
        {
            Load(selected);
            if (expectedRevision != revision) throw new InvalidOperationException("Route revision changed. Request map state again.");
            if (offset < 0 || offset > items.Length) throw new ArgumentException("Invalid route chunk offset.");
            var page = new List<RouteMapItem>();
            for (var i = offset; i < items.Length && page.Count < 40; i++)
            {
                page.Add(items[i]);
                if (Encoding.UTF8.GetByteCount(JsonSerializer.Serialize(page, WireJson.Options)) > 6200) { page.RemoveAt(page.Count - 1); break; }
            }
            return new(revision, offset, page.ToArray(), offset + page.Count, offset + page.Count == items.Length);
        }
    }
    public object Plans(int offset) => coordinator.Store.Plans(10, Math.Max(0, offset)).Select(p => new { p.Id, p.Name, p.Mode,Waypoints=p.Waypoints?.Length??0,Patterns=p.Patterns?.Length??0,Instruments=Short(string.Join(", ",(p.Instruments??[]).Where(i=>i.Enabled).Select(i=>i.InstrumentId)),100) }).ToArray();
}
