using System.IO.Compression;
using AeroScience.Engine;
using Xunit;
namespace AeroScience.Tests;
public sealed class BetaFeedbackTests
{
    [Fact] public void DefaultReportContainsOnlySelectedTextAndBuildInformation()
    {
        using var stream=new MemoryStream();BetaFeedback.Write(stream,new("QA-flight","Expected recording","Observed pause label","Click pause","Optional aircraft"));stream.Position=0;
        using var zip=new ZipArchive(stream);Assert.Equal(new[]{"build.json","REPORT.txt"},zip.Entries.Select(e=>e.FullName));
        using var reader=new StreamReader(zip.GetEntry("build.json")!.Open());var text=reader.ReadToEnd();Assert.Contains("0.8.0-beta.1",text);Assert.Contains("\"uploaded\":false",text);Assert.Contains("\"location_history_included\":false",text);
    }
    [Theory][InlineData("password=secret")][InlineData("Bearer token")]
    public void CredentialsAreRefusedBeforeWriting(string secret)
    {using var stream=new MemoryStream();Assert.Throws<ArgumentException>(()=>BetaFeedback.Write(stream,new("QA",secret,"Observed","Steps","")));Assert.Equal(0,stream.Length);}
    [Fact] public void ScientificFilesCannotBeDisguisedAsScreenshots()
    {
        var path=Path.GetTempFileName();try{File.WriteAllText(path,"LAS original scientific evidence");using var output=new MemoryStream();Assert.Throws<IOException>(()=>BetaFeedback.Write(output,new("QA","Expected","Observed","Steps",""),path));Assert.Equal(0,output.Length);}finally{File.Delete(path);}
    }
}
