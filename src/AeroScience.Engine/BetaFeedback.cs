using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
namespace AeroScience.Engine;

public sealed record BetaFeedbackReport(string Area,string Expected,string Actual,string Steps,string Configuration);

// Deliberately minimal beta QA export. No mission/science/organization services,
// logs, location history, environment dump, network request or scientific credit.
public static class BetaFeedback
{
    public static void Write(Stream output,BetaFeedbackReport report,string? screenshot=null)
    {
        var fields=new[]{report.Area,report.Expected,report.Actual,report.Steps,report.Configuration};
        if(fields.Any(v=>v is null||v.Length>4000)||fields.All(string.IsNullOrWhiteSpace))throw new ArgumentException("Describe the issue using at most 4,000 characters per field.");
        if(fields.Any(v=>Regex.IsMatch(v,@"(?i)bearer\s+\S+|password\s*[:=]|access_token\s*[:=]")))throw new ArgumentException("Review the report and remove credentials before saving.");
        byte[]? picture=null;string? extension=null;
        if(screenshot is not null)
        {
            var info=new FileInfo(screenshot);if(!info.Exists||(info.Attributes&FileAttributes.ReparsePoint)!=0||info.Length is <8 or >10485760)throw new IOException("Choose one reviewed PNG/JPEG image of at most 10 MiB.");
            picture=File.ReadAllBytes(screenshot);
            extension=picture.AsSpan(0,8).SequenceEqual(new byte[]{137,80,78,71,13,10,26,10})?"png":picture[0]==255&&picture[1]==216?"jpg":throw new IOException("Only actual PNG/JPEG screenshots are accepted.");
        }
        using var zip=new ZipArchive(output,ZipArchiveMode.Create,true);
        void Text(string name,string value){using var writer=new StreamWriter(zip.CreateEntry(name).Open(),new UTF8Encoding(false));writer.Write(value);}
        Text("build.json",JsonSerializer.Serialize(new{version=ApplicationProfile.BetaVersion,channel="beta",source_commit=ApplicationProfile.SourceCommit,
            exported_utc=DateTimeOffset.UtcNow,os=RuntimeInformation.OSDescription,runtime=RuntimeInformation.FrameworkDescription,
            automatic_diagnostics=false,original_files_included=false,location_history_included=false,uploaded=false}));
        Text("REPORT.txt",$"AeroScience v{ApplicationProfile.BetaVersion} — beta QA (no scientific/organization credit)\nArea/card: {report.Area}\nExpected:\n{report.Expected}\nObserved:\n{report.Actual}\nSteps:\n{report.Steps}\nOptional aircraft/MSFS/hardware configuration:\n{report.Configuration}\n\nReview every file before manually sharing. No report was uploaded.\nScreenshots may include personal information or embedded metadata.\n");
        if(picture is not null){using var image=zip.CreateEntry("reviewed-screenshot."+extension).Open();image.Write(picture);}
    }
}
