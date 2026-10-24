using System.Text.Json;
using AeroScience.Engine;
using AeroScience.Missions;
using AeroScience.Shared;
using Microsoft.Data.Sqlite;
using NetTopologySuite.IO;

namespace AeroScience.Tests;

// Explicit public-original acceptance; never invoked by an ordinary map read.
public static class AirspaceReferenceProbe
{
    private sealed class Offline : HttpMessageHandler
    { public int Calls; protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage r,CancellationToken t){Calls++;throw new IOException("Offline original verification must not access the network.");} }
    public static async Task Run(string[] args)
    {
        if(args.Length!=2||args[0] is not("network" or "offline"))throw new ArgumentException("Use network/offline and an isolated evidence directory.");
        var root=Path.GetFullPath(args[1]);var network=args[0]=="network";
        if(network){if(Directory.Exists(root))throw new IOException("Choose a new genuine evidence directory; existing originals are preserved.");Directory.CreateDirectory(root);}
        var offline=new Offline();using var http=network?new HttpClient(new HttpClientHandler{AllowAutoRedirect=false}){Timeout=TimeSpan.FromSeconds(30)}:new HttpClient(offline);
        using var service=new AviationReference(root,root,http,unmetered:()=>true);
        Dictionary<string,string> Interpretations()
        {
            var values=new Dictionary<string,string>();var path=Path.Combine(root,"AviationReference","aviation.db");if(!File.Exists(path))return values;
            using var db=new SqliteConnection("Data Source="+path+";Mode=ReadOnly;Pooling=False");db.Open();using var command=db.CreateCommand();command.CommandText="SELECT id,payload FROM vector_datasets ORDER BY id";
            var datasets=new List<(string Id,string Payload)>();using(var rows=command.ExecuteReader())while(rows.Read())datasets.Add((rows.GetString(0),rows.GetString(1)));
            foreach(var d in datasets)
            {
                using var hash=System.Security.Cryptography.IncrementalHash.CreateHash(System.Security.Cryptography.HashAlgorithmName.SHA256);
                void Add(byte[] data){hash.AppendData(BitConverter.GetBytes(data.Length));hash.AppendData(data);}
                Add(System.Text.Encoding.UTF8.GetBytes(d.Payload));command.CommandText="SELECT id,geometry,display,payload FROM airspace_features WHERE dataset_id=$id ORDER BY id";command.Parameters.Clear();command.Parameters.AddWithValue("$id",d.Id);
                using var rows=command.ExecuteReader();while(rows.Read()){Add(System.Text.Encoding.UTF8.GetBytes(rows.GetString(0)));Add(rows.GetFieldValue<byte[]>(1));Add(rows.IsDBNull(2)?[]:rows.GetFieldValue<byte[]>(2));Add(System.Text.Encoding.UTF8.GetBytes(rows.GetString(3)));}
                values[d.Id]=Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
            }
            return values;
        }
        var historical=Interpretations();
        if(network)
        {
            service.ConfigureDownloads(false,null);service.Configure(true);var catalog=await service.Refresh("nasr");
            var selected=catalog.Charts.Where(i=>i.Edition=="2026-10-01"&&AviationAirspace.IsOriginal(i)).ToArray();
            if(selected.Length!=2)throw new IOException("Both reviewed 2026-10-01 Class/SAA originals are required; no silent cycle substitution.");
            foreach(var item in selected)
            {
                var p=await service.PreviewDownload("nasr",item.Id,268435456,536870912);service.DownloadControl(p.Id,p.Revision,"approve",p.Preview.SelectionHash);await service.ProcessDownloads();
                if(service.DownloadJobs().Single(j=>j.Id==p.Id).State!="Completed")throw new IOException("Genuine FAA acquisition failed; inspect the durable job before retrying.");
            }
        }
        else service.Configure(false);
        var results=new List<object>();
        foreach(var job in service.DownloadJobs().Where(j=>j.State=="Completed"&&AviationAirspace.IsOriginal(j.Preview.Item)))
        {
            await service.VerifyDownload(job.Id);var dataset=await service.ImportVectors(job.Id);
            var folder=Path.Combine(root,"AviationReference","Originals",job.Id.ToString("N"),"files");
            using var db=new SqliteConnection("Data Source="+Path.Combine(root,"AviationReference","aviation.db")+";Mode=ReadOnly;Pooling=False");db.Open();
            using var cmd=db.CreateCommand();cmd.CommandText="SELECT geometry FROM airspace_features WHERE dataset_id=$id";cmd.Parameters.AddWithValue("$id",dataset.Id);
            using var rows=cmd.ExecuteReader();var retained=new List<byte[]>();while(rows.Read())retained.Add(rows.GetFieldValue<byte[]>(0));
            var reader=new WKBReader();var writer=new WKBWriter();var current=new List<byte[]>();var omitted=0;var omissions=new List<object>();
            foreach(var record in dataset.Family=="CLASS"?AviationAirspace.ReadClass(folder,job,default):AviationAirspace.ReadSaa(folder,job,default))
            {if(record.UnsupportedReason is not null){omitted++;omissions.Add(new{record.Number,record.Name,record.Attributes,reason=record.UnsupportedReason});continue;}current.Add(writer.Write(record.Original!));}
            static string[] Keys(IEnumerable<byte[]> values)=>values.Select(v=>Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(v))).Order(StringComparer.Ordinal).ToArray();
            if(!Keys(current).SequenceEqual(Keys(retained))||current.Count!=dataset.Features||omitted!=dataset.OmittedRecords)throw new IOException("Current parser failed to reproduce every retained original geometry/omission.");
            var east=service.AirspaceFeatures(dataset.Id,-73,41,-70,43);var west=service.AirspaceFeatures(dataset.Id,-124,32,-114,42);
            if(east.Features.Length==0||west.Features.Length==0)throw new IOException("Missing genuine reference features in one of two required regions.");
            results.Add(new{dataset,currentParserGeometryMatches=true,omitted,omissions,eastFeatures=east.Features.Length,westFeatures=west.Features.Length});
        }
        if(results.Count!=2||offline.Calls!=0||File.Exists(Path.Combine(root,"missions.db")))throw new IOException("Incomplete Class/SAA inventory, unexpected offline network or mission creation.");
        var after=Interpretations();if(historical.Any(p=>!after.TryGetValue(p.Key,out var hash)||hash!=p.Value))throw new IOException("A historical interpretation changed during additive reimport.");
        File.WriteAllText(Path.Combine(root,"airspace-"+args[0]+"-receipt.json"),JsonSerializer.Serialize(new{evidenceClass="Genuine FAA originals / real application parser, verified files, durable aviation store and offline reconstruction; not activation/geodetic/owner-device acceptance",results,offlineNetworkCalls=offline.Calls,historicalInterpretationsSha256=historical,historicalInterpretationsPreserved=true},WireJson.Options));
        Console.WriteLine("PASS: both genuine FAA airspace families reproduced offline, with explicit unsupported records and no science credit.");
    }
}
