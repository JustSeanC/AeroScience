using System.Globalization;
using System.Text;
using System.Text.Json;
using AeroScience.Shared;
using Microsoft.Data.Sqlite;

namespace AeroScience.Missions;

// Query projection only. The six original stores remain authoritative; no new history or raw cache.
public sealed partial class MissionStore
{
    const string IngressReason="Committed exact ingress and eligible acquisition opportunities; no interpolated observations.";
    internal const int TimelineRowBytes=262144;
    internal static string TimelineBoundedColumn(string column)=>$"CASE WHEN length(CAST({column} AS BLOB))<={TimelineRowBytes} THEN {column} END";
    internal static string TimelineOriginal(SqliteDataReader rows,int index)=>rows.IsDBNull(index)?throw new NotSupportedException("An original record exceeds the bounded timeline limit. Narrow the UTC interval or use the existing archive/export view."):rows.GetString(index);
    internal sealed record TimelineCursor(int Version,Guid FlightId,string Scope,DateTimeOffset At,MissionTimelineSource Source,string Id);
    // JSON UTC omits zero/trailing fractional digits; SQL and the merged axis must compare
    // the same 100 ns UTC representation without rounding through SQLite julianday.
    internal static string TimelineCollectionUtc=>"substr(json_extract(payload_json,'$.inputUtc'),1,19)||'.'||substr((CASE WHEN substr(json_extract(payload_json,'$.inputUtc'),20,1)='.' THEN replace(replace(substr(json_extract(payload_json,'$.inputUtc'),21),'+00:00',''),'Z','') ELSE '' END)||'0000000',1,7)||'+00:00'";
    static void InitializeTimelineIndexes(SqliteConnection db)
    {
        using var command=db.CreateCommand();command.CommandText=$"""
            CREATE INDEX IF NOT EXISTS collection_timeline_time ON collection_events(mission_id,({TimelineCollectionUtc}),sequence);
            CREATE INDEX IF NOT EXISTS events_kind_time ON events(mission_id,kind,timestamp_utc,id);
            CREATE INDEX IF NOT EXISTS collection_timeline_changes ON collection_events(mission_id,({TimelineCollectionUtc}),sequence)
              WHERE json_extract(payload_json,'$.reason') <> 'Committed exact ingress and eligible acquisition opportunities; no interpolated observations.';
            CREATE INDEX IF NOT EXISTS personal_card_event_reference ON collection_receipts(mission_id,json_extract(result_json,'$.eventId')) WHERE kind='personal-card';
            CREATE INDEX IF NOT EXISTS personal_attempt_begin_reference ON personal_card_attempts(mission_id,json_extract(payload_json,'$.beginEventId'));
            CREATE INDEX IF NOT EXISTS personal_attempt_end_reference ON personal_card_attempts(mission_id,json_extract(payload_json,'$.endEventId'));
            """;command.ExecuteNonQuery();
    }
    internal static string TimelineHash(string json)=>Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(json)));
    internal static string TimelineEventHash(MissionEvent e)=>PreflightReadiness.Hash(e);
    (DateTimeOffset StartedUtc,DateTimeOffset? EndedUtc,MissionStatus Status) TimelineFlight(Guid id)
    {using var db=Open();using var cmd=db.CreateCommand();cmd.CommandText="SELECT started_utc,ended_utc,status FROM missions WHERE id=$id";cmd.Parameters.AddWithValue("$id",id.ToString());using var row=cmd.ExecuteReader();if(!row.Read())throw new ArgumentException("Flight not found.");return(Parse(row.GetString(0)),row.IsDBNull(1)?null:Parse(row.GetString(1)),Enum.Parse<MissionStatus>(row.GetString(2)));}
    internal static string TimelineScope(Guid id,MissionTimelineQuery q)=>PreflightReadiness.Hash(new{id,q.FromUtc,q.ToUtc,q.Lane,q.CardId,q.SessionId,q.InstrumentId,q.Recent});
    internal static TimelineCursor? TimelineAfter(Guid id,MissionTimelineQuery q)
    {
        if(q.Limit is <1 or >100||q.InstrumentId?.Length>100||q.FromUtc>q.ToUtc||q.FromUtc?.Offset!=null&&q.FromUtc.Value.Offset!=TimeSpan.Zero||q.ToUtc?.Offset!=null&&q.ToUtc.Value.Offset!=TimeSpan.Zero||q.Lane is {} lane&&!Enum.IsDefined(lane))throw new ArgumentException("Choose a UTC interval, supported category and a page of 1–100 original events.");
        if(q.Cursor is null)return null;if(q.Cursor.Length>2048)throw new ArgumentException("This timeline cursor is too large.");
        try{var text=q.Cursor.Replace('-','+').Replace('_','/');text=text.PadRight((text.Length+3)/4*4,'=');var c=JsonSerializer.Deserialize<TimelineCursor>(Convert.FromBase64String(text),WireJson.Options)!;
            if(c is null||c.Version!=1||c.FlightId!=id||c.Scope!=TimelineScope(id,q)||!Enum.IsDefined(c.Source)||c.At.Offset!=TimeSpan.Zero||c.Id.Length>100)throw new ArgumentException("The timeline selection changed. Start its first page again.");return c;
        }catch(Exception e)when(e is FormatException or JsonException or NullReferenceException){throw new ArgumentException("The timeline cursor is invalid. Start its first page again.");}
    }
    internal static void TimelineParameters(SqliteCommand cmd,Guid id,MissionTimelineQuery q,TimelineCursor? cursor,MissionTimelineSource source)
    {
        cmd.Parameters.AddWithValue("$flight",id.ToString());cmd.Parameters.AddWithValue("$from",Stamp(q.FromUtc??DateTimeOffset.MinValue));cmd.Parameters.AddWithValue("$to",Stamp(q.ToUtc??DateTimeOffset.MaxValue));
        cmd.Parameters.AddWithValue("$limit",q.Limit+1);cmd.Parameters.AddWithValue("$card",q.CardId?.ToString()??"");cmd.Parameters.AddWithValue("$session",q.SessionId?.ToString()??"");cmd.Parameters.AddWithValue("$instrument",q.InstrumentId??"");
        if(cursor is null)return;cmd.Parameters.AddWithValue("$at",Stamp(cursor.At));cmd.Parameters.AddWithValue("$rank",(int)cursor.Source);
        object key=source is MissionTimelineSource.FlightEvent or MissionTimelineSource.CollectionEvent or MissionTimelineSource.Telemetry or MissionTimelineSource.ScienceObservation?cursor.Source==source&&long.TryParse(cursor.Id,out var number)&&number>0?number:0:cursor.Source==source&&Guid.TryParse(cursor.Id,out var guid)?guid.ToString():"";cmd.Parameters.AddWithValue("$key",key);
    }
    internal static string TimelineWhere(string time,string key,MissionTimelineSource source,MissionTimelineQuery q,TimelineCursor? cursor)
    {
        var range=$"{time}>=$from AND {time}<=$to";if(cursor is null)return range;var op=q.Recent?"<":">";
        return range+$" AND ({time}{op}$at OR ({time}=$at AND ({(int)source}{op}$rank OR ({(int)source}=$rank AND {key}{op}$key))))";
    }
    internal static string TimelineOrder(string time,string key,MissionTimelineQuery q)=>$" ORDER BY {time} {(q.Recent?"DESC":"ASC")},{key} {(q.Recent?"DESC":"ASC")} LIMIT $limit";
    static string TimelineSortKey(MissionTimelineItem row)=>row.Reference.Source is MissionTimelineSource.PilotMarker or MissionTimelineSource.ResearchNote?row.Reference.Id:long.Parse(row.Reference.Id,CultureInfo.InvariantCulture).ToString("D20",CultureInfo.InvariantCulture);
    internal static Guid? TimelineGuid(SqliteDataReader r,int index)=>r.IsDBNull(index)?null:Guid.Parse(r.GetString(index));
    static string FriendlyEvent(string kind)=>kind switch{"started"=>"Flight recording started","completed"=>"Flight recording ended","interrupted"=>"Flight recording interrupted","connection"=>"Simulator connection changed","operating-assessment"=>"Instrument operating assessment","payload-snapshot"=>"Frozen instrument configuration","science-deleted"=>"Collected science permanently deleted",_=>kind.Replace('-',' ')};
    public MissionTimelinePage MissionTimeline(Guid id,MissionTimelineQuery query,ScienceStore science,CancellationToken token=default)
    {
        token.ThrowIfCancellationRequested();var flight=TimelineFlight(id);var cursor=TimelineAfter(id,query);var deleted=ScienceDeleted(id);var items=new List<MissionTimelineItem>();var limits=new List<string>{
            "This bounded page contains original references, not complete-flight coverage. Point samples remain points; no interpolation or sensor footprint is inferred.",
            "Card/session/instrument filters match explicit stored identities only. Unassociated records are excluded rather than linked by a nearby timestamp.",
            "Published quality is shown on the timeline; selecting a measurement checks its retained original acquisition receipt. Nearby time-window context is separately labeled."};
        using(var db=Open()){
            if(query.Lane is null or MissionTimelineLane.Flight or MissionTimelineLane.ResearchCards or MissionTimelineLane.Measurements or MissionTimelineLane.ScienceSessions or MissionTimelineLane.ObservationsWarnings)
            {
                using var cmd=db.CreateCommand();var where=TimelineWhere("e.timestamp_utc","e.id",MissionTimelineSource.FlightEvent,query,cursor);
                var lane="CASE WHEN e.kind LIKE 'research-card-%' THEN 'ResearchCards' WHEN e.kind='operating-assessment' THEN 'Measurements' WHEN e.kind LIKE 'science-%' THEN 'ScienceSessions' WHEN e.kind IN ('started','completed','interrupted','paused','resumed','payload-snapshot','instruments-configured') THEN 'Flight' ELSE 'ObservationsWarnings' END";
                cmd.CommandText=$"""
                    SELECT e.id,e.timestamp_utc,e.kind,{TimelineBoundedColumn("e.message")},
                      coalesce(json_extract(r.result_json,'$.cardId'),a.card_id,b.card_id),coalesce(a.id,b.id),
                      coalesce(json_extract(a.payload_json,'$.testPointId'),json_extract(b.payload_json,'$.testPointId')),
                      CASE WHEN e.kind='operating-assessment' THEN json_extract(e.message,'$.assessment.sessionId') END,
                      CASE WHEN e.kind='operating-assessment' THEN json_extract(e.message,'$.configuration.settings.instrumentId') END
                    FROM events e
                    LEFT JOIN collection_receipts r ON r.mission_id=e.mission_id AND r.kind='personal-card' AND json_extract(r.result_json,'$.eventId')=e.id
                    LEFT JOIN personal_card_attempts a ON a.mission_id=e.mission_id AND json_extract(a.payload_json,'$.beginEventId')=e.id
                    LEFT JOIN personal_card_attempts b ON b.mission_id=e.mission_id AND json_extract(b.payload_json,'$.endEventId')=e.id
                    WHERE e.mission_id=$flight AND {where}
                      AND ($lane='' OR {lane}=$lane)
                      AND ($card='' OR coalesce(json_extract(r.result_json,'$.cardId'),a.card_id,b.card_id)=$card)
                      AND ($session='' OR CASE WHEN e.kind='operating-assessment' THEN json_extract(e.message,'$.assessment.sessionId') END=$session)
                      AND ($instrument='' OR CASE WHEN e.kind='operating-assessment' THEN json_extract(e.message,'$.configuration.settings.instrumentId') END=$instrument)
                    """+TimelineOrder("e.timestamp_utc","e.id",query);
                TimelineParameters(cmd,id,query,cursor,MissionTimelineSource.FlightEvent);cmd.Parameters.AddWithValue("$lane",query.Lane?.ToString()??"");using var cancel=token.Register(cmd.Cancel);using var rows=cmd.ExecuteReader();while(rows.Read()){
                    token.ThrowIfCancellationRequested();var kind=rows.GetString(2);if(deleted&&kind=="operating-assessment")continue;var raw=TimelineOriginal(rows,3);var l=kind.StartsWith("research-card-",StringComparison.Ordinal)?MissionTimelineLane.ResearchCards:kind=="operating-assessment"?MissionTimelineLane.Measurements:kind.StartsWith("science-",StringComparison.Ordinal)?MissionTimelineLane.ScienceSessions:kind is "started" or "completed" or "interrupted" or "paused" or "resumed" or "payload-snapshot" or "instruments-configured"?MissionTimelineLane.Flight:MissionTimelineLane.ObservationsWarnings;
                    items.Add(new(new(MissionTimelineSource.FlightEvent,rows.GetInt64(0).ToString(CultureInfo.InvariantCulture),TimelineEventHash(new(rows.GetInt64(0),Parse(rows.GetString(1)),kind,raw))),Parse(rows.GetString(1)),l,FriendlyEvent(kind),kind is "operating-assessment" or "payload-snapshot" or "instruments-configured" or "mission-performance-report"?"Original configuration/assessment retained. Select to inspect its exact source.":raw.Length>300?raw[..300]+"…":raw,TimelineGuid(rows,4),TimelineGuid(rows,7),rows.IsDBNull(8)?null:rows.GetString(8),TimelineGuid(rows,5),TimelineGuid(rows,6)));
                }
            }
            if((query.Lane is null or MissionTimelineLane.ScienceSessions)&&query.CardId is null&&query.InstrumentId is null){
                using var cmd=db.CreateCommand();var time="("+TimelineCollectionUtc+")";cmd.CommandText=$"SELECT sequence,{TimelineBoundedColumn("payload_json")} FROM collection_events WHERE mission_id=$flight AND {TimelineWhere(time,"sequence",MissionTimelineSource.CollectionEvent,query,cursor)} AND json_extract(payload_json,'$.reason') <> '{IngressReason}' AND ($session='' OR session_id=$session)"+TimelineOrder(time,"sequence",query);TimelineParameters(cmd,id,query,cursor,MissionTimelineSource.CollectionEvent);using var cancel=token.Register(cmd.Cancel);using var rows=cmd.ExecuteReader();while(rows.Read()){token.ThrowIfCancellationRequested();var raw=TimelineOriginal(rows,1);var e=EvidenceContracts.Read<CollectionEvent>(raw);if(e.MissionId!=id||e.Sequence!=rows.GetInt64(0))throw new InvalidOperationException("Original collection event identity differs from its stored reference.");items.Add(new(new(MissionTimelineSource.CollectionEvent,e.Sequence.ToString(CultureInfo.InvariantCulture),TimelineHash(raw)),e.InputUtc,MissionTimelineLane.ScienceSessions,"Science "+e.Action,e.Outcome+" · "+e.From+" → "+e.To+" · "+e.Reason,SessionId:e.SessionId));}
            }
            if((query.Lane is null or MissionTimelineLane.Flight)&&query.CardId is null&&query.SessionId is null&&query.InstrumentId is null){
                using var cmd=db.CreateCommand();cmd.CommandText=$"SELECT sequence,timestamp_utc,{TimelineBoundedColumn("payload_json")} FROM telemetry WHERE mission_id=$flight AND {TimelineWhere("timestamp_utc","sequence",MissionTimelineSource.Telemetry,query,cursor)}"+TimelineOrder("timestamp_utc","sequence",query);TimelineParameters(cmd,id,query,cursor,MissionTimelineSource.Telemetry);using var cancel=token.Register(cmd.Cancel);using var rows=cmd.ExecuteReader();while(rows.Read()){token.ThrowIfCancellationRequested();var raw=TimelineOriginal(rows,2);var f=JsonSerializer.Deserialize<TelemetryFrame>(raw,WireJson.Options)!;if(f.TimestampUtc!=Parse(rows.GetString(1)))throw new InvalidOperationException("Original telemetry UTC differs from its indexed record.");items.Add(new(new(MissionTimelineSource.Telemetry,rows.GetInt64(0).ToString(CultureInfo.InvariantCulture),TimelineHash(raw)),f.TimestampUtc,MissionTimelineLane.Flight,"Recorded flight sample","Original aircraft telemetry; not a scientific sensor footprint.",Latitude:f.Latitude,Longitude:f.Longitude,AltitudeMslFeet:f.AltitudeMslFeet));}
            }
            if((query.Lane is null or MissionTimelineLane.ObservationsWarnings)&&query.SessionId is null&&query.InstrumentId is null)foreach(var source in new[]{MissionTimelineSource.PilotMarker,MissionTimelineSource.ResearchNote}){
                using var cmd=db.CreateCommand();var table=source==MissionTimelineSource.PilotMarker?"research_markers":"research_notes";var cards=source==MissionTimelineSource.PilotMarker?"$.pilotObservation.cards":"$.observationCards";cmd.CommandText=$"SELECT id,timestamp_utc,{TimelineBoundedColumn("payload_json")} FROM {table} WHERE mission_id=$flight AND {TimelineWhere("timestamp_utc","id",source,query,cursor)} AND ($card='' OR EXISTS(SELECT 1 FROM json_each(payload_json,'{cards}') WHERE json_extract(value,'$.id')=$card))"+TimelineOrder("timestamp_utc","id",query);TimelineParameters(cmd,id,query,cursor,source);using var cancel=token.Register(cmd.Cancel);using var rows=cmd.ExecuteReader();while(rows.Read()){token.ThrowIfCancellationRequested();var raw=TimelineOriginal(rows,2);if(source==MissionTimelineSource.PilotMarker){var p=JsonSerializer.Deserialize<MissionMarker>(raw,WireJson.Options)!;if(p.MissionId!=id||p.Id.ToString()!=rows.GetString(0)||p.TimestampUtc!=Parse(rows.GetString(1)))throw new InvalidOperationException("Original pilot observation identity differs from its index.");items.Add(new(new(source,p.Id.ToString(),TimelineHash(raw)),p.TimestampUtc,MissionTimelineLane.ObservationsWarnings,"Pilot Observation — "+p.Category,(p.Notes is {} note?note[..Math.Min(300,note.Length)]:"Location marker without a written note"),Latitude:p.Latitude,Longitude:p.Longitude,AltitudeMslFeet:p.AltitudeMslFeet));}else{var n=JsonSerializer.Deserialize<MissionNote>(raw,WireJson.Options)!;if(n.MissionId!=id||n.Id.ToString()!=rows.GetString(0)||n.TimestampUtc!=Parse(rows.GetString(1)))throw new InvalidOperationException("Original note identity differs from its index.");items.Add(new(new(source,n.Id.ToString(),TimelineHash(raw)),n.TimestampUtc,MissionTimelineLane.ObservationsWarnings,"Pilot research note",n.Text.Length>300?n.Text[..300]+"…":n.Text));}}
            }
        }
        if(!deleted&&query.CardId is null&&(query.Lane is null or MissionTimelineLane.Measurements)){try{items.AddRange(science.TimelineObservations(id,query,cursor,token));}catch(Exception e)when(e is IOException or SqliteException or UnauthorizedAccessException){limits.Add("Scientific originals are unavailable: "+e.Message);}}
        if(deleted)limits.Add("Collected scientific evidence was permanently deleted. Its measurements and derived results are unavailable; raw flight recording is separate.");
        if(!deleted&&ScienceDeleted(id)){deleted=true;items.RemoveAll(x=>x.Reference.Source==MissionTimelineSource.ScienceObservation||x.Title=="Instrument operating assessment");}
        var sorted=items.OrderBy(x=>x.TimestampUtc).ThenBy(x=>x.Reference.Source).ThenBy(TimelineSortKey,StringComparer.Ordinal);var page=(query.Recent?sorted.Reverse():sorted).Take(query.Limit+1).ToArray();var selected=page.Take(query.Limit).ToArray();var next=page.Length>query.Limit&&selected.LastOrDefault() is {} last?Convert.ToBase64String(JsonSerializer.SerializeToUtf8Bytes(new TimelineCursor(1,id,TimelineScope(id,query),last.TimestampUtc,last.Reference.Source,last.Reference.Id),WireJson.Options)).TrimEnd('=').Replace('+','-').Replace('/','_'):null;
        return new(1,id,DateTimeOffset.UtcNow,flight.StartedUtc,flight.EndedUtc,flight.Status==MissionStatus.Recording,deleted,selected,next,limits.ToArray());
    }
}

public sealed partial class ScienceStore
{
    internal MissionTimelineItem[] TimelineObservations(Guid mission,MissionTimelineQuery query,MissionStore.TimelineCursor? cursor,CancellationToken token)
    {
        if(AvailabilityError() is {} unavailable)throw new IOException(unavailable);if(!File.Exists(DatabasePath))return [];
        using var db=Open();using var cmd=db.CreateCommand();cmd.CommandText=$"SELECT id,timestamp_utc,{MissionStore.TimelineBoundedColumn("payload_json")} FROM observations WHERE mission_id=$flight AND {MissionStore.TimelineWhere("timestamp_utc","id",MissionTimelineSource.ScienceObservation,query,cursor)} AND ($instrument='' OR json_extract(payload_json,'$.instrumentId')=$instrument) AND ($session='' OR json_extract(payload_json,'$.sessionId')=$session)"+MissionStore.TimelineOrder("timestamp_utc","id",query);MissionStore.TimelineParameters(cmd,mission,query,cursor,MissionTimelineSource.ScienceObservation);using var cancel=token.Register(cmd.Cancel);using var rows=cmd.ExecuteReader();var result=new List<MissionTimelineItem>();while(rows.Read()){
            token.ThrowIfCancellationRequested();var raw=MissionStore.TimelineOriginal(rows,2);var o=ReadCatalog<ScienceObservation>(raw)with{Id=rows.GetInt64(0)};if(o.MissionId!=mission||o.TimestampUtc!=DateTimeOffset.Parse(rows.GetString(1),CultureInfo.InvariantCulture))throw new InvalidOperationException("Original scientific observation identity differs from its index.");result.Add(new(new(MissionTimelineSource.ScienceObservation,o.Id.ToString(CultureInfo.InvariantCulture),MissionStore.TimelineHash(raw)),o.TimestampUtc,MissionTimelineLane.Measurements,o.MeasurementId+" observation","Original recorded reading · published quality "+o.Quality+" · "+o.Provenance,SessionId:o.SessionId,InstrumentId:o.InstrumentId,MeasurementId:o.MeasurementId,Value:double.IsFinite(o.Value)?o.Value:null,Unit:o.Unit,Quality:o.Quality,Provenance:o.Provenance,Latitude:o.Latitude,Longitude:o.Longitude,AltitudeMslFeet:o.AltitudeMslFeet));
        }return result.ToArray();
    }
}
