using AeroScience.Shared;

namespace AeroScience.Missions;

public sealed record ScientificSourceCollection(string Id,RemoteKind Kind,string Provider,string Product,long Regions,long MatchingRegions,long Bytes,string? LatestObservation);
public sealed record ScientificSourceCollectionPage(ScientificSourceCollection[] Items,string? Next,long Total,long Regions=0,long Bytes=0);
public sealed partial class ScienceStore
{
    public ScientificSourceCollectionPage SourceCollections(SourceQuery? query=null,string? after=null,int limit=20,string sort="name")
    {
        if(limit is <1 or >50||sort is not("name" or "date" or "size")||after is not null&&(!long.TryParse(after,out var value)||value<0))throw new ArgumentException("Use a collection cursor, supported sort and 1–50 collections per page.");
        var offset=after is null?0:long.Parse(after,System.Globalization.CultureInfo.InvariantCulture);
        EnsureSourceIndex();using var db=Open();using var command=db.CreateCommand();var where=SourceWhere(command,query);
        command.CommandText="SELECT COUNT(DISTINCT collection_key) FROM source_search i WHERE "+where;var total=(long)command.ExecuteScalar()!;
        command.CommandText="SELECT COUNT(*),COALESCE(SUM(managed_bytes),0) FROM source_search i WHERE "+where;long regions,bytes;using(var totals=command.ExecuteReader()){totals.Read();regions=totals.GetInt64(0);bytes=totals.GetInt64(1);}
        command.CommandText="""
            SELECT i.collection_key,i.kind,i.provider,i.product,
              (SELECT COUNT(*) FROM source_search all_regions WHERE all_regions.collection_key=i.collection_key),COUNT(*),
              (SELECT SUM(managed_bytes) FROM source_search all_regions WHERE all_regions.collection_key=i.collection_key),MAX(i.observation_utc)
            FROM source_search i WHERE
            """+" "+where+" GROUP BY i.collection_key ORDER BY "+(sort=="size"?"7 DESC, i.collection_key":sort=="date"?"8 DESC, i.collection_key":"i.product COLLATE NOCASE, i.collection_key")+" LIMIT $limit OFFSET $after";
        command.Parameters.AddWithValue("$after",offset);command.Parameters.AddWithValue("$limit",limit+1);
        var items=new List<ScientificSourceCollection>();string? next=null;
        using var rows=command.ExecuteReader();while(rows.Read())
        {
            if(items.Count==limit){next=(offset+items.Count).ToString(System.Globalization.CultureInfo.InvariantCulture);break;}
            items.Add(new(rows.GetString(0),(RemoteKind)rows.GetInt32(1),rows.GetString(2),rows.GetString(3),rows.GetInt64(4),rows.GetInt64(5),rows.GetInt64(6),rows.IsDBNull(7)?null:rows.GetString(7)));
        }
        return new(items.ToArray(),next,total,regions,bytes);
    }
}
