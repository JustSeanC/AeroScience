using AeroScience.Shared;
namespace AeroScience.Missions;

// Archive work only: never called inside the coordinator/telemetry lock.
public static class ScientificEvaluationEvidence
{
    public static StoredScienceIntent[] Intents(MissionStore store,Guid mission)
    {
        var result=new List<StoredScienceIntent>();
        for(var offset=0;;offset+=500){var page=store.ScienceIntents(mission,limit:500,offset:offset);result.AddRange(page);if(result.Count>200000)throw new InvalidOperationException("Evaluation exceeds 200,000 acquisition intents; no partial report is accepted.");if(page.Length<500)break;}
        return result.ToArray();
    }
    public static AcquisitionResult[] Read(MissionStore missions,ScienceStore science,Guid mission,CancellationToken token=default)
    {
        var intents=Intents(missions,mission);var rows=new Dictionary<Guid,ScienceObservation[]>();var products=new Dictionary<Guid,RemoteProduct>();var libraryAvailable=true;
        try
        {
            rows=science.StreamObservations(mission).Where(o=>o.IntentId.HasValue&&o.Calibration is null).Take(2000001).GroupBy(o=>o.IntentId!.Value).ToDictionary(g=>g.Key,g=>g.ToArray());
            if(rows.Sum(g=>g.Value.Length)>2000000)throw new InvalidOperationException("Evaluation exceeds two million original scalar values; no partial result accepted.");
            products=science.AllProducts(mission).Take(200001).ToDictionary(p=>p.Id);if(products.Count>200000)throw new InvalidOperationException("Evaluation exceeds 200,000 products.");
        }
        catch(Exception e)when(e is IOException or Microsoft.Data.Sqlite.SqliteException){libraryAvailable=false;}
        var results=new List<AcquisitionResult>();
        foreach(var stored in intents)
        {
            token.ThrowIfCancellationRequested();var intent=stored.Intent;var references=new[]{"intent:"+intent.Id, "input:"+intent.InputReceiptId};
            var reconstructed=stored.State=="Reconstructed";
            if(RemoteAcquisition.Instruments.ContainsKey(intent.InstrumentId))
            {
                if(libraryAvailable&&stored.ProductId is {} id&&products.TryGetValue(id,out var product))
                {
                    if(product.IntentId!=intent.Id||product.SessionId!=intent.SessionId||product.Sha256!=stored.ProductSha256)throw new InvalidDataException("Published product identity differs from its frozen acquisition intent.");
                    var data=science.ReadProduct(id);var original=data.Bands.Where(b=>b.Id is not("QA_PIXEL" or "QA_RADSAT" or "SCL")).ToArray();
                    var expected=original.Sum(b=>b.Values.Length);var valid=original.Sum(b=>b.Values.Count(v=>v.HasValue&&double.IsFinite(v.Value)));
                    results.Add(new(intent.Id,intent.SessionId,intent.InstrumentId,intent.ExposureUtc,expected,valid,expected-valid,true,true,reconstructed,[..references,"product:"+id+":"+product.Sha256,"source:"+product.SourceId],product.Warnings,product.ProcessedUtc));
                }
                else results.Add(new(intent.Id,intent.SessionId,intent.InstrumentId,intent.ExposureUtc,0,0,0,false,false,reconstructed,references,[libraryAvailable?"No verified original scientific product; pending, failed or source-unavailable result remains unassessed.":"Scientific Library unavailable; retained mission intents are not scientific results."]));
            }
            else
            {
                var expected=ScienceCatalog.Definitions.Single(d=>d.Id==intent.InstrumentId).Measurements.Length;
                var originals=rows.GetValueOrDefault(intent.Id,[]).Where(r=>r.SessionId==intent.SessionId&&r.Calibration is null).GroupBy(r=>r.MeasurementId).Select(g=>g.First()).ToArray();
                var valid=originals.Count(o=>o.Quality==ObservationQuality.Valid&&double.IsFinite(o.Value));
                var input=missions.CollectionInput(mission,intent.InputReceiptId)?.Packet;
                var assessable=libraryAvailable&&(input?.Atmosphere is not null||input?.Frame?.Atmosphere is not null);
                results.Add(new(intent.Id,intent.SessionId,intent.InstrumentId,intent.ExposureUtc,expected,valid,expected-valid,assessable,originals.Length>0,reconstructed,references,[]));
            }
        }
        return results.ToArray();
    }
}
