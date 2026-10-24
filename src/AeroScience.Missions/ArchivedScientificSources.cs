using AeroScience.Shared;
using System.Text.Json;

namespace AeroScience.Missions;

public sealed record ArchivedSourceQuery(Guid[]? Missions=null,string? Instrument=null,long After=0,int Limit=20,Guid? SourceId=null);
public sealed record ArchivedSourceItem(ScientificSource Source,bool OriginalsRetained);
public sealed record ArchivedSourcePage(ArchivedSourceItem[] Items,long? Next,long Total);
public sealed partial class ScienceStore
{
    public ArchivedSourcePage ArchivedSources(ArchivedSourceQuery query)
    {
        if(query.Missions?.Length>100||query.After<0||query.Limit is <1 or >40)throw new ArgumentException("Use a bounded archived-source page and at most 100 specific missions.");
        using var db=Open();using var command=db.CreateCommand();var where="($instrument='' OR instrument_id=$instrument) AND ($source='' OR json_extract(payload_json,'$.sourceId')=$source)";
        command.Parameters.AddWithValue("$instrument",query.Instrument??"");command.Parameters.AddWithValue("$source",query.SourceId?.ToString()??"");
        if(query.Missions is{Length:>0}){var names=query.Missions.Select((id,index)=>{var name="$mission"+index;command.Parameters.AddWithValue(name,id.ToString());return name;});where+=" AND mission_id IN ("+string.Join(',',names)+")";}
        command.CommandText="SELECT COUNT(DISTINCT json_extract(payload_json,'$.sourceId')) FROM remote_products WHERE "+where;var total=Convert.ToInt64(command.ExecuteScalar());
        command.CommandText="SELECT MIN(rowid),id FROM remote_products WHERE "+where+" GROUP BY json_extract(payload_json,'$.sourceId') HAVING MIN(rowid)>$after ORDER BY MIN(rowid) LIMIT $limit";command.Parameters.AddWithValue("$after",query.After);command.Parameters.AddWithValue("$limit",query.Limit+1);
        var representatives=new List<(long Row,Guid Id)>();using(var rows=command.ExecuteReader())while(rows.Read())representatives.Add((rows.GetInt64(0),Guid.Parse(rows.GetString(1))));
        var items=new List<ArchivedSourceItem>();long? next=null;long last=query.After,bytes=0;
        foreach(var (row,id) in representatives){if(items.Count==query.Limit){next=last;break;}var saved=ReadProduct(id).Source;var size=JsonSerializer.SerializeToUtf8Bytes(saved,WireJson.Options).LongLength;if(items.Count>0&&bytes+size>16777216){next=last;break;}items.Add(new(saved,Source(saved.Id) is not null));bytes+=size;last=row;}
        return new(items.ToArray(),next,total);
    }
}
