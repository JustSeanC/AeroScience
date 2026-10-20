using System.Globalization;
using System.Text;
using System.Xml.Linq;
using System.Text.RegularExpressions;
using AeroScience.Shared;

namespace AeroScience.Missions;

public sealed record RouteExportRequest(MissionPlan Plan, string Format = "pln", string CoordinateFormat = "decimal",
    string RouteStyle = "coordinates", bool AcknowledgeLongRoute = false);
public sealed record RouteExportPreview(string Name, string? Departure, string? Destination, int Waypoints,
    double DistanceMetres, string[] Patterns, string Format, string[] Warnings);
public sealed record RouteExportResult(RouteExportPreview Preview, string Content, string ContentType, string FileName);

public static class GeographicCoordinates
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
    // Round the entire angle first, so 59.999999 seconds carries into minutes/degrees.
    public static string Angle(double value, bool latitude, string format)
    {
        if (!double.IsFinite(value) || Math.Abs(value) > (latitude ? 90 : 180)) throw new ArgumentException("Invalid WGS84 coordinate.");
        var hemisphere = value < 0 ? (latitude ? "S" : "W") : (latitude ? "N" : "E");
        var width = latitude ? 2 : 3;
        if (format == "decimal") return (value == 0 ? 0 : value).ToString("0.00000000", Inv);
        if (format == "ddm")
        {
            var minutes = Math.Round(Math.Abs(value) * 60, 4, MidpointRounding.AwayFromZero);
            return hemisphere + ((int)(minutes / 60)).ToString(new string('0', width), Inv) + (minutes % 60).ToString("00.0000", Inv);
        }
        if (format is not ("dms" or "icao" or "simbrief")) throw new ArgumentException("Choose decimal, ddm, dms, icao or simbrief coordinates.");
        var seconds = Math.Round(Math.Abs(value) * 3600, format == "dms" ? 3 : 0, MidpointRounding.AwayFromZero);
        var degrees = ((int)(seconds / 3600)).ToString(new string('0', width), Inv);
        var minutesPart = ((int)(seconds % 3600 / 60)).ToString("00", Inv);
        var secondsPart = (seconds % 60).ToString(format == "dms" ? "00.000" : "00", Inv);
        if (format == "dms") return $"{hemisphere}{degrees}° {minutesPart}' {secondsPart}\"";
        if (format == "simbrief") return degrees + minutesPart + secondsPart + hemisphere;
        // ICAO Item 15 coordinate notation supports degrees/minutes, not fractional minutes.
        var roundedMinutes = Math.Round(Math.Abs(value) * 60, 0, MidpointRounding.AwayFromZero);
        return ((int)(roundedMinutes / 60)).ToString(new string('0', width), Inv) + ((int)(roundedMinutes % 60)).ToString("00", Inv) + hemisphere;
    }
    public static string Pair(ResearchWaypoint w, string format) => Angle(w.Latitude, true, format) + (format is "icao" or "simbrief" ? "" : format == "decimal" ? ", " : " ") + Angle(w.Longitude, false, format);
    public static double ParseAngle(string input, bool latitude, string format)
    {
        if (format == "decimal") { var v = double.Parse(input, Inv); _ = Angle(v, latitude, "decimal"); return v; }
        var h = input.FirstOrDefault(c => "NSEW".Contains(c));
        if (!(latitude ? "NS" : "EW").Contains(h) || h == '\0') throw new ArgumentException("Coordinate hemisphere is missing or incorrect.");
        var numbers = Regex.Matches(input, @"[0-9]+(?:\.[0-9]+)?").Select(m => m.Value).ToArray();
        double value;
        if (format == "dms")
        {
            if (numbers.Length != 3 || double.Parse(numbers[1], Inv) >= 60 || double.Parse(numbers[2], Inv) >= 60) throw new ArgumentException("Invalid DMS coordinate.");
            value = double.Parse(numbers[0], Inv) + double.Parse(numbers[1], Inv) / 60 + double.Parse(numbers[2], Inv) / 3600;
        }
        else
        {
            var width = latitude ? 2 : 3; var digits = string.Concat(numbers);
            var degrees = double.Parse(digits[..width], Inv);
            var minutes = double.Parse(digits.Substring(width, format == "ddm" ? digits.Length - width : 2), Inv);
            var seconds = format == "simbrief" ? double.Parse(digits[(width + 2)..], Inv) : 0;
            if (minutes >= 60 || seconds >= 60) throw new ArgumentException("Invalid geographic minutes/seconds.");
            value = degrees + minutes / 60 + seconds / 3600;
        }
        value *= h is 'S' or 'W' ? -1 : 1; _ = Angle(value, latitude, "decimal"); return value;
    }
}

public static class RouteExports
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
    private static string Number(double n) => n.ToString("0.########", Inv);
    public static RouteExportResult Generate(RouteExportRequest request)
    {
        var plan = MissionPlanning.Validate(request.Plan); var points = plan.Waypoints!;
        if (points.Length == 0) throw new ArgumentException("Add at least one waypoint before exporting the planned route.");
        var warnings = new List<string> { "Planned route only. Science configuration, notes, observations and survey metadata remain in AeroScience; this does not file a flight plan or control the aircraft." };
        if ((plan.Patterns?.Length ?? 0) > 0 && !(request.Format=="pln"&&plan.Patterns!.Any(p=>p.Kind==FlightPatternKind.StraightTransect&&p.Passes>1))) warnings.Add("Pattern geometry is exported as saved ordered waypoints. No simplification or densification is performed.");
        if (points.Length > 100) warnings.Add("Large route: all waypoints are retained without simplification. EFB/FMS/import limits vary; real MSFS import testing is pending. Coordinate route fields may exceed external application limits.");
        if (request.Format == "pln" && points.Length > 100 && !request.AcknowledgeLongRoute) throw new ArgumentException("This route has more than 100 waypoints. Acknowledge the large-route warning to export every point unchanged; the EFB's practical limit has not been established.");
        if (points.Any(w => w.TargetAltitudeFeet is null)) warnings.Add("Unspecified waypoint altitudes are omitted; they are not replaced by zero or by the cruising altitude.");
        if (plan.Departure is null or "" || plan.Destination is null or "") warnings.Add("No airport is invented: the EFB permits omitted departure/destination IDs and uses the first/last en-route waypoint.");
        if (request.CoordinateFormat == "icao") warnings.Add("ICAO degrees/minutes rounds to one minute (up to about 0.5 NM per axis). Use decimal, DDM or DMS for precise surveys.");
        if (request.CoordinateFormat == "simbrief") warnings.Add("SimBrief documents DDMMSSNDDDMMSSW custom coordinates. Values round to whole arcseconds (up to about 15.5 m per axis); use decimal/DDM/DMS lists or PLN for precise surveys. Route acceptance and aircraft/FMS behavior still require testing in the target application; no automatic submission occurs.");
        if(request.Format=="pln" && plan.Patterns?.Any(p=>p.Kind==FlightPatternKind.StraightTransect&&p.Passes>1)==true)
        {
            warnings.Add("Repeated endpoints are not validated in MSFS EFB/FMS. Navigation export includes the first A/B for each repeated transect only. Fly the native/CSV pass instructions with pilot-managed turnarounds.");
            points=points.Where(w=>w.PatternId is not {} id || plan.Patterns.FirstOrDefault(p=>p.Id==id) is not {Kind:FlightPatternKind.StraightTransect,Passes:>1} || Array.IndexOf(plan.Waypoints!,w)==Array.FindIndex(plan.Waypoints!,p=>p.PatternId==id) || Array.IndexOf(plan.Waypoints!,w)==Array.FindIndex(plan.Waypoints!,p=>p.PatternId==id)+1).ToArray();
            plan=plan with{Waypoints=points};
        }
        var distance = points.Zip(points.Skip(1), (a, b) => Geodesy.Distance(Geodesy.Point(a), Geodesy.Point(b))).Sum();
        var preview = new RouteExportPreview(plan.Name, plan.Departure, plan.Destination, points.Length, distance,
            (plan.Patterns ?? []).Select(p => p.Kind.ToString()).Distinct().ToArray(), request.Format, warnings.ToArray());
        string content, type;
        switch (request.Format)
        {
            case "pln": content = Pln(plan); type = "application/xml"; break;
            case "gpx": content = Gpx(plan); type = "application/gpx+xml"; break;
            case "kml": content = Kml(plan); type = "application/vnd.google-earth.kml+xml"; break;
            case "csv": content = Csv(plan); type = "text/csv; charset=utf-8"; break;
            case "coordinates": content = string.Join("\n", points.Select(w => GeographicCoordinates.Pair(w, request.CoordinateFormat))); type = "text/plain; charset=utf-8"; break;
            case "route":
                if (request.RouteStyle == "named")
                {
                    warnings.Add("Published navigation identifiers are user-supplied and have not been checked against a navigation database; this string does not encode the science waypoints' precise coordinates.");
                    if (points.Any(w => w.NavigationIdentifier is null || !Regex.IsMatch(w.NavigationIdentifier, "^[A-Z0-9]{2,8}$"))) throw new ArgumentException("Named routes require an explicitly entered published navigation identifier on every waypoint. Science waypoint names are not assumed to be published fixes.");
                    content = string.Join(" DCT ", points.Select(w => w.NavigationIdentifier));
                }
                else if (request.RouteStyle == "detailed") content = plan.Name + "\n" + string.Join("\n", points.Select((w, i) => $"{i + 1}. {w.Name}: {GeographicCoordinates.Pair(w, "decimal")}; altitude {(w.TargetAltitudeFeet is { } a ? Number(a) + " ft MSL" : "unspecified")}; {w.Instructions}"));
                else if (request.RouteStyle == "coordinates")
                {
                    if (request.CoordinateFormat is not ("icao" or "simbrief")) throw new ArgumentException("For a flight-planning coordinate route, select ICAO minutes or documented SimBrief seconds. Use Copy Waypoint Coordinates for decimal/DDM/DMS lists.");
                    content = string.Join(" DCT ", points.Select(w => GeographicCoordinates.Pair(w, request.CoordinateFormat)));
                }
                else throw new ArgumentException("Choose a supported route string style.");
                type = "text/plain; charset=utf-8"; break;
            default: throw new ArgumentException("Unknown planned-route export format.");
        }
        // Never use user-provided text as an output path.
        return new(preview with { Warnings = warnings.ToArray() }, content, type, $"AeroScience-plan-{plan.Id:N}.{(request.Format is "route" or "coordinates" ? "txt" : request.Format)}");
    }
    private static string Xml(XElement root) => new XDocument(new XDeclaration("1.0", "utf-8", null), root).ToString();
    private static string Pln(MissionPlan plan)
    {
        if (plan.CruisingAltitudeFeet is not { } cruise || !double.IsFinite(cruise) || cruise is < -2000 or > 100000) throw new ArgumentException("Enter the required flight-plan cruising altitude in feet MSL before exporting PLN.");
        if (plan.FlightRules is not ("VFR" or "IFR")) throw new ArgumentException("Choose VFR or IFR flight rules.");
        var p = new XElement("FlightPlan.FlightPlan", new XElement("Title", plan.Name), new XElement("Descr", string.IsNullOrWhiteSpace(plan.Description) ? "AeroScience planned research route" : plan.Description), new XElement("FPType", plan.FlightRules), new XElement("CruisingAlt", Number(cruise)), new XElement("AppVersion", new XElement("AppVersionMajor", 12)));
        foreach (var (name, value) in new[] { ("DepartureID", plan.Departure), ("DestinationID", plan.Destination) })
            if (!string.IsNullOrWhiteSpace(value))
            {
                if (!Regex.IsMatch(value, "^[A-Za-z0-9]{2,8}$")) throw new ArgumentException("For PLN, enter a 2–8 character airport identifier or leave it empty for a local research flight. Descriptive airport names are not ICAO IDs.");
                p.Add(new XElement(name, value.ToUpperInvariant()));
            }
        foreach (var (w, index) in plan.Waypoints!.Select((w, i) => (w, i)))
        {
            var position = GeographicCoordinates.Pair(w, "dms").Replace("\" ", "\", ");
            if (w.TargetAltitudeFeet is { } alt) position += "," + alt.ToString("+000000.00;-000000.00;+000000.00", Inv);
            p.Add(new XElement("ATCWaypoint", new XAttribute("id", w.Name), new XElement("ATCWaypointType", "User"), new XElement("WorldPosition", position), new XElement("ICAO", new XElement("ICAOIdent", "AS" + (index + 1).ToString("000000", Inv)))));
        }
        return Xml(new XElement("SimBase.Document", p));
    }
    private static string Gpx(MissionPlan p)
    {
        XNamespace ns = "http://www.topografix.com/GPX/1/1";
        XElement Point(string tag, ResearchWaypoint w) => new(ns + tag, new XAttribute("lat", Number(w.Latitude)), new XAttribute("lon", Number(w.Longitude)), w.TargetAltitudeFeet is { } a ? new XElement(ns + "ele", Number(a * .3048)) : null, new XElement(ns + "name", w.Name), new XElement(ns + "desc", w.Instructions ?? ""));
        return Xml(new XElement(ns + "gpx", new XAttribute("version", "1.1"), new XAttribute("creator", "AeroScience"), p.Waypoints!.Select(w => Point("wpt", w)), new XElement(ns + "rte", new XElement(ns + "name", p.Name), p.Waypoints!.Select(w => Point("rtept", w)))));
    }
    private static string Kml(MissionPlan p)
    {
        XNamespace ns = "http://www.opengis.net/kml/2.2";
        string Coordinate(ResearchWaypoint w) => Number(w.Longitude) + "," + Number(w.Latitude) + (w.TargetAltitudeFeet is { } a ? "," + Number(a * .3048) : "");
        var doc = new XElement(ns + "Document", new XElement(ns + "name", p.Name));
        // The line is a ground projection; individual points retain only explicitly specified altitudes.
        doc.Add(new XElement(ns + "Placemark", new XElement(ns + "name", "Planned route (ground projection)"), new XElement(ns + "LineString", new XElement(ns + "altitudeMode", "clampToGround"), new XElement(ns + "coordinates", string.Join(" ", p.Waypoints!.Select(w => Number(w.Longitude) + "," + Number(w.Latitude)))))));
        foreach (var w in p.Waypoints!) doc.Add(new XElement(ns + "Placemark", new XElement(ns + "name", w.Name), new XElement(ns + "description", w.Instructions ?? ""), new XElement(ns + "Point", new XElement(ns + "altitudeMode", w.TargetAltitudeFeet is null ? "clampToGround" : "absolute"), new XElement(ns + "coordinates", Coordinate(w)))));
        foreach (var area in p.SurveyAreas ?? []) doc.Add(new XElement(ns + "Placemark", new XElement(ns + "name", area.Name), new XElement(ns + "Polygon", new XElement(ns + "altitudeMode", "clampToGround"), new XElement(ns + "outerBoundaryIs", new XElement(ns + "LinearRing", new XElement(ns + "coordinates", string.Join(" ", area.Vertices.Append(area.Vertices[0]).Select(v => Number(v.Longitude) + "," + Number(v.Latitude)))))))));
        return Xml(new XElement(ns + "kml", doc));
    }
    private static string Csv(MissionPlan p)
    {
        static string Field(string? s) { s ??= ""; if (s.TrimStart().FirstOrDefault() is '=' or '+' or '-' or '@' || s.StartsWith('\t') || s.StartsWith('\r')) s = "'" + s; return "\"" + s.Replace("\"", "\"\"") + "\""; }
        return "order,waypoint_id,name,latitude_deg,longitude_deg,target_altitude_ft_msl,pattern_id,navigation_identifier,instructions,objective\r\n" + string.Join("\r\n", p.Waypoints!.Select((w, i) => string.Join(",", (i + 1).ToString(Inv), Field(w.Id?.ToString() ?? $"{p.Id:N}:{i + 1}"), Field(w.Name), Number(w.Latitude), Number(w.Longitude), w.TargetAltitudeFeet is { } a ? Number(a) : "", Field(w.PatternId?.ToString()), Field(w.NavigationIdentifier), Field(w.Instructions), Field(w.Objective)))) + "\r\n";
    }
}
