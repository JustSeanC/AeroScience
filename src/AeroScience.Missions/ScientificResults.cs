using AeroScience.Shared;
namespace AeroScience.Missions;
public sealed partial class ScienceStore
{
    public long ProductCount(Guid? mission=null,string? instrument=null)
    {
        if(AvailabilityError() is {} unavailable)throw new IOException(unavailable);
        if(!File.Exists(DatabasePath))return 0;
        using var db=Open();using var cmd=db.CreateCommand();cmd.CommandText="SELECT name FROM sqlite_master WHERE name='remote_products'";if(cmd.ExecuteScalar() is null)return 0;
        cmd.CommandText="SELECT COUNT(*) FROM remote_products WHERE ($mission='' OR mission_id=$mission) AND ($instrument='' OR instrument_id=$instrument)";cmd.Parameters.AddWithValue("$mission",mission?.ToString()??"");cmd.Parameters.AddWithValue("$instrument",instrument??"");return Convert.ToInt64(cmd.ExecuteScalar());
    }
    public object ProductLocations(Guid mission,string? instrument,int offset)
    {
        var products=Products(mission,10,offset,instrument);
        var items=products.Select(p=>
        {
            var data=ReadProduct(p.Id);
            var locations=data.SampleLocations.Where((_,i)=>data.Bands.Where(b=>b.Id is not("QA_PIXEL" or "QA_RADSAT" or "SCL")).Any(b=>b.Values[i].HasValue)).ToArray();
            return new{p.Id,p.InstrumentId,p.FlightPacketUtc,p.SourceObservationUtc,p.Footprint,locations};
        }).ToArray();
        return new{items,total=ProductCount(mission,instrument),offset,hasMore=offset+items.Length<ProductCount(mission,instrument)};
    }
}
