using AeroScience.Shared;

namespace AeroScience.Missions;

// Source-specific, conservatively inset main-body clips reviewed against retained FAA sheets.
// These are NOT publisher neatline records. Unreviewed rasters/editions remain unsupported.
// Full edge, inset, overlap and printed-control acceptance is deliberately still required.
public sealed record AviationChartDefinition(string FileName, string Sha256, int Width, int Height,
    AviationChartRaster.Pixel[] MainBody, string Evidence)
{
    public string Id => Sha256[..24] + "-" + FileIdentity(FileName) + "-interior1";
    public static string FileIdentity(string name) => Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(name))).ToLowerInvariant()[..8];
}

public static class AviationChartSheets
{
    public const string Limitations = "Conservatively clipped main-body interior reviewed from this exact FAA original. Full chart edges are excluded; this is not an authoritative publisher neatline or certified navigation. Printed control-point, inset and full-edition acceptance remain pending. Cartographic colors are not scientific measurements.";
    private static AviationChartRaster.Pixel[] Box(double x1, double y1, double x2, double y2) => [new(x1,y1),new(x2,y1),new(x2,y2),new(x1,y2),new(x1,y1)];
    public static readonly AviationChartDefinition[] Reviewed =
    [
        new("Hawaiian Islands SEC.tif", "949162cb2db19f8d69c23387b07ca08a76a62bf36f9e2fed72d64c97cb2993e7", 20186,16370,
            [new(5159,1346),new(18112,10315),new(15532,14408),new(2131,5494),new(5159,1346)], "2026-09-03 original; reviewed rotated main-body interior, separate inset TIFFs are not projected here."),
        new("Boston TAC.tif", "f5600b60ccdc691153c04a66bf73832e5620ecf26ccf9f2b0ffadaf792cced6d",12478,8969,
            Box(1706,416,10120,8703), "2026-09-03 original; reviewed interior excludes legend, side notices, lower US overview and coordinate-label margins."),
        new("Boston HEL.tif", "97f57c5635b71916f8b5525cd8f772d34125a1a9b561c9bc0f20d4e939adebf2",10491,8691,
            Box(1632,408,8801,8392), "2026-09-03 original; reviewed main interior, separate Downtown inset is not substituted."),
        new("ENR_L01.tif", "05b6606761c5b9f1b62d6ad5fe6b2cfacf054ce114469f8689ddec35359e146a",22000,8000,
            Box(2383,427,21694,7573), "2026-09-03 original; reviewed interior excludes cover, US overview, index and coordinate-label margins."),
        new("ENR_H01.tif", "04284d394be73501e79337dcdb231376ba02de23a5984c8cfa962257003385f6",24000,8000,
            Box(2400,400,23733,7666), "2026-09-03 original; reviewed interior excludes cover, US overview, index and coordinate-label margins.")
    ];
    public static AviationChartDefinition? Match(AviationRetainedFile file) => Reviewed.SingleOrDefault(d => d.Sha256 == file.Sha256 && d.FileName == file.Name);
}

public interface IAviationChartRenderer
{
    bool Available { get; }
    AviationChartRaster.Description Describe(string path, AviationChartDefinition definition, CancellationToken token);
    byte[] Tile(string path, AviationChartDefinition definition, int zoom, int x, int y, CancellationToken token);
}
