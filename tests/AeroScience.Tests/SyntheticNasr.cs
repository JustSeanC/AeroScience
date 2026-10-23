using System.IO.Compression;
using System.Text;
namespace AeroScience.Tests;

// Explicit synthetic development records. No genuine FAA scientific/geographic acceptance.
public static class SyntheticNasr
{
    public const string Date = "2026/10/01";
    public const string Cycle = "2026-10-01";
    public static string Url(string family) => $"https://nfdc.faa.gov/webContent/28DaySub/extra/01_Oct_2026_{family}_CSV.zip";
    public static Dictionary<string, string> Files(string family)
    {
        const string coordinateHeader = "LAT_DEG,LAT_MIN,LAT_SEC,LAT_HEMIS,LAT_DECIMAL,LONG_DEG,LONG_MIN,LONG_SEC,LONG_HEMIS,LONG_DECIMAL";
        const string coordinates = "40,0,0,N,40,75,0,0,W,-75";
        if (family == "FIX") return new() { ["FIX_BASE.csv"] = "EFF_DATE,FIX_ID,ICAO_REGION_CODE,STATE_CODE,COUNTRY_CODE," + coordinateHeader + "\n" + Date + ",TEST1,K6,PA,US," + coordinates + "\n" + Date + ",TEST2,K2,CA,US,37,0,0,N,37,122,0,0,W,-122\n" };
        if (family == "NAV") return new() { ["NAV_BASE.csv"] = "EFF_DATE,NAV_ID,NAV_TYPE,STATE_CODE,COUNTRY_CODE,CITY,NAME," + coordinateHeader + "\n" + Date + ",TEST,VOR,PA,US,PHILADELPHIA,Synthetic navaid," + coordinates + "\n" };
        return new() {
            ["APT_BASE.csv"] = "EFF_DATE,SITE_NO,SITE_TYPE_CODE,STATE_CODE,COUNTRY_CODE,ARPT_NAME,ARPT_ID," + coordinateHeader + "\n" + Date + ",001,A,PA,US,\"Synthetic, Field\",TEST," + coordinates + "\n" + Date + ",002,H,PA,US,Synthetic heliport,HEL," + coordinates + "\n",
            ["APT_RWY_END.csv"] = "EFF_DATE,SITE_NO,SITE_TYPE_CODE,STATE_CODE,COUNTRY_CODE,ARPT_ID,RWY_ID,RWY_END_ID,RWY_END_LAT_DEG,RWY_END_LAT_MIN,RWY_END_LAT_SEC,RWY_END_LAT_HEMIS,LAT_DECIMAL,RWY_END_LONG_DEG,RWY_END_LONG_MIN,RWY_END_LONG_SEC,RWY_END_LONG_HEMIS,LONG_DECIMAL\n" + Date + ",001,A,PA,US,TEST,18/36,18,40,0,0,N,40,75,0,0,W,-75\n" + Date + ",001,A,PA,US,TEST,18/36,36,40,0,36,N,40.01,75,0,0,W,-75\n",
            ["APT_RWY.csv"] = "EFF_DATE,SITE_NO,SITE_TYPE_CODE,COUNTRY_CODE,ARPT_ID,RWY_ID,RWY_LEN,RWY_WIDTH\n" + Date + ",001,A,US,TEST,18/36,4000,100\n" };
    }
    public static byte[] Archive(string family)
    {
        using var bytes = new MemoryStream();
        using (var zip = new ZipArchive(bytes, ZipArchiveMode.Create, true)) foreach (var file in Files(family))
        { using var writer = new StreamWriter(zip.CreateEntry(file.Key).Open(), new UTF8Encoding(false)); writer.Write(file.Value); }
        return bytes.ToArray();
    }
}
