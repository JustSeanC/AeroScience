using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Xml.Linq;
using AeroScience.Shared;

namespace AeroScience.Missions;

public static class RemoteExports
{
    public static string Content(RemoteProductData data, string format)
    {
        var p = data.Product;
        if (format == "json") return JsonSerializer.Serialize(data, WireJson.Options);
        if (format == "source.json") return JsonSerializer.Serialize(data.Source, WireJson.Options);
        var metadata = new { formatVersion=1,source=data.Source.Manifest,p.SourceId,p.SourceClass,p.Provenance,p.Quality,p.Warnings,
            p.FlightPacketUtc,p.SourceObservationUtc,p.ProcessedUtc,horizontalCrs="EPSG:4326",p.Footprint.VerticalReference,
            bands=data.Bands.Select(b=>new{b.Id,b.Unit,b.WavelengthNm,b.OriginalGrid,b.SamplingMethod}),data.ProcessingMethods,
            missingData="Null means outside source coverage, nodata, bad band, quality excluded or invalid; never zero-filled",mode=data.Evidence?.Mode };
        if (format == "evidence.json") return JsonSerializer.Serialize(new
        {
            formatVersion = 1, kind = "AeroScience portable scientific evidence", evidence = data.Evidence,
            product = p, source = new { data.Source.Id, data.Source.Manifest, data.Source.ManifestSha256, data.Source.Files },
            processing = data.ProcessingMethods,
            sample = new { location = data.SampleLocations[data.SampleLocations.Length / 2], bands = data.Bands.Select(b => new { b.Id, b.Unit, b.WavelengthNm, value = b.Values[b.Values.Length / 2], b.OriginalGrid }) },
            limitations = "Local evidence and hashes are integrity aids, not tamper-proof proof that a flight occurred. Independent evaluation requires sufficient original samples/source data. Nothing is uploaded automatically."
        }, WireJson.Options);
        if (format == "geojson") return JsonSerializer.Serialize(new
        {
            type = "FeatureCollection", metadata, features = new object[] { new { type = "Feature", geometry = new { type = "Polygon", coordinates = new[] { p.Footprint.Boundary.Append(p.Footprint.Boundary[0]).Select(c => new[] { c.Longitude, c.Latitude }).ToArray() } },
                properties = new { p.Id, p.SourceId, p.InstrumentId, p.FlightPacketUtc, p.SourceObservationUtc, p.ProcessedUtc, p.Provenance, p.Footprint.Method, p.Footprint.Limitations } } }
                .Concat(data.SampleLocations.Select((point, i) => new { type = "Feature", geometry = new { type = "Point", coordinates = new[] { point.Longitude, point.Latitude } }, properties = new { sample = i, values = data.Bands.ToDictionary(b => b.Id, b => b.Values[i]), derived = data.Derived.ToDictionary(b => b.Key, b => b.Value[i]) } })).ToArray()
        }, WireJson.Options);
        if (format == "gpx" || format == "kml")
        {
            if (format == "gpx")
            {
                XNamespace ns = "http://www.topografix.com/GPX/1/1";
                return new XDocument(new XElement(ns + "gpx", new XAttribute("version", "1.1"), new XAttribute("creator", "AeroScience"),new XElement(ns+"metadata",new XElement(ns+"desc",JsonSerializer.Serialize(metadata,WireJson.Options))), new XElement(ns + "rte", new XElement(ns + "name", p.InstrumentId + " approximate observation footprint"), p.Footprint.Boundary.Append(p.Footprint.Boundary[0]).Select(c => new XElement(ns + "rtept", new XAttribute("lat", c.Latitude.ToString("R", CultureInfo.InvariantCulture)), new XAttribute("lon", c.Longitude.ToString("R", CultureInfo.InvariantCulture))))))).ToString();
            }
            XNamespace kml = "http://www.opengis.net/kml/2.2";
            var coordinates = string.Join(" ", p.Footprint.Boundary.Append(p.Footprint.Boundary[0]).Select(c => c.Longitude.ToString("R", CultureInfo.InvariantCulture) + "," + c.Latitude.ToString("R", CultureInfo.InvariantCulture)));
            var ring = new XElement(kml + "outerBoundaryIs", new XElement(kml + "LinearRing", new XElement(kml + "coordinates", coordinates)));
            var polygon = new XElement(kml + "Polygon", new XElement(kml + "altitudeMode", "clampToGround"), ring);
            var placemark = new XElement(kml + "Placemark", new XElement(kml + "name", p.InstrumentId + " approximate observation footprint"), new XElement(kml + "description", JsonSerializer.Serialize(metadata,WireJson.Options)), polygon);
            return new XDocument(new XElement(kml + "kml", new XElement(kml + "Document", placemark))).ToString();
        }
        if (format != "csv") throw new ArgumentException("Supported product exports: json, source.json, evidence.json, csv, geojson, gpx and kml. GeoTIFF/LAS output is not implemented.");
        static string Cell(object? value)
        {
            var text = value is double n ? n.ToString("R", CultureInfo.InvariantCulture) : Convert.ToString(value, CultureInfo.InvariantCulture) ?? "";
            if (value is string && text.TrimStart().FirstOrDefault() is '=' or '+' or '-' or '@') text = "'" + text;
            return "\"" + text.Replace("\"", "\"\"") + "\"";
        }
        var csv = new StringBuilder("sample,latitude,longitude,band,wavelength_nm,value,unit,source_observation_utc,flight_packet_utc,processed_utc,source_id,source_vertical_datum,source_scene,source_crs,pixel_width,pixel_height,sampling_method,provenance,quality,missing_data_rule,algorithm_version,science_mode,flight_source,footprint_altitude_reference\r\n");
        for (var i = 0; i < data.SampleLocations.Length; i++)
            foreach (var b in data.Bands.Concat(data.Derived.Select(v => new RemoteBandValues(v.Key, v.Key == "temperature" ? "K" : v.Key == "elevation" ? "m" : "1", null, v.Value, "derived", data.Bands[0].OriginalGrid))))
                csv.AppendLine(string.Join(',', new object?[] { i, data.SampleLocations[i].Latitude, data.SampleLocations[i].Longitude, b.Id, b.WavelengthNm, b.Values[i], b.Unit, p.SourceObservationUtc?.ToString("O"), p.FlightPacketUtc.ToString("O"), p.ProcessedUtc.ToString("O"), p.SourceId, data.Source.Manifest.VerticalDatum,data.Source.Manifest.SceneId,b.OriginalGrid.Crs,b.OriginalGrid.PixelWidth,b.OriginalGrid.PixelHeight,b.SamplingMethod,p.Provenance,p.Quality,"Empty means unavailable/quality excluded, never zero-filled","remote-sampling-1.0",data.Evidence?.Mode,data.Evidence?.Aircraft.Source,p.Footprint.VerticalReference }.Select(Cell)));
        return csv.ToString();
    }
}
