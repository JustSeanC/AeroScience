using System.Globalization;
using System.Text;
using System.Text.Json;
using AeroScience.Shared;
using Microsoft.Data.Sqlite;
namespace AeroScience.Missions;

public sealed record RecordedMissionOperations(int Version,Mission Mission,MissionResearch Research,
    ResearchFlightState Cards,CollectionOverview Collection,InstrumentOperatingReport[] Operating,
    ScientificReviewStatus Review,bool ScienceDeleted,string[] Limitations);
public sealed record MissionOperationsStatus(int Version,Guid FlightId,MissionStatus Status,bool ScienceDeleted,ScientificReviewStatus Review);
public sealed record MissionTimelineEvidence(int Version,Guid FlightId,MissionTimelineReference Reference,
    DateTimeOffset TimestampUtc,string Integrity,string ContextMethod,MissionEvent? Event=null,
    CollectionEvent? CollectionEvent=null,ReplaySample? Telemetry=null,ScienceObservation? Observation=null,
    MissionMarker? Marker=null,MissionNote? Note=null,RecordedResearchAttempt? Attempt=null,
    PersonalResearchAttempt? SavedResult=null,PersonalResearchCard? Card=null,
    ScienceCollectionSession? Session=null,StoredScienceIntent? Acquisition=null,
    CollectionInputReceipt? OriginalInput=null,InstrumentOperatingReport? Operating=null,
    ReplaySample[]? ContextTelemetry=null,long? NextTelemetrySequence=null,
    MissionTimelineItem[]? ContextMeasurements=null,string[]? Limitations=null);

public sealed partial class MissionStore
{
    public MissionTimelinePointer TimelineReference(Guid flight,MissionTimelineSource source,string id,ScienceStore science)
    {
        _=TimelineFlight(flight);if(!Enum.IsDefined(source)||id.Length>100)throw new ArgumentException("Choose an original evidence identity.");if(source==MissionTimelineSource.ScienceObservation){if(ScienceDeleted(flight))throw new InvalidOperationException("Collected science was permanently deleted.");return science.TimelineObservationReference(flight,id);}
        using var db=Open();using var cmd=db.CreateCommand();cmd.Parameters.AddWithValue("$flight",flight.ToString());var numeric=source is MissionTimelineSource.FlightEvent or MissionTimelineSource.CollectionEvent or MissionTimelineSource.Telemetry;
        if(numeric){if(!long.TryParse(id,out var n)||n<1)throw new ArgumentException("Choose a numeric original evidence identity.");cmd.Parameters.AddWithValue("$id",n);}else{if(!Guid.TryParse(id,out var g)||g==Guid.Empty)throw new ArgumentException("Choose an original note/marker identity.");cmd.Parameters.AddWithValue("$id",g.ToString());}
        cmd.CommandText=source switch{MissionTimelineSource.FlightEvent=>"SELECT timestamp_utc,message,kind FROM events WHERE mission_id=$flight AND id=$id",MissionTimelineSource.CollectionEvent=>"SELECT json_extract(payload_json,'$.inputUtc'),payload_json,'' FROM collection_events WHERE mission_id=$flight AND sequence=$id",MissionTimelineSource.Telemetry=>"SELECT timestamp_utc,payload_json,'' FROM telemetry WHERE mission_id=$flight AND sequence=$id",MissionTimelineSource.PilotMarker=>"SELECT timestamp_utc,payload_json,'' FROM research_markers WHERE mission_id=$flight AND id=$id",MissionTimelineSource.ResearchNote=>"SELECT timestamp_utc,payload_json,'' FROM research_notes WHERE mission_id=$flight AND id=$id",_=>throw new ArgumentException("Unsupported source.")};using var row=cmd.ExecuteReader();if(!row.Read())throw new InvalidOperationException("The original evidence was deleted or is unavailable.");var at=Parse(row.GetString(0));var raw=row.GetString(1);if(Encoding.UTF8.GetByteCount(raw)>TimelineRowBytes)throw new NotSupportedException("This original is too large for bounded timeline inspection.");return new(new(source,id,source==MissionTimelineSource.FlightEvent?TimelineEventHash(new(long.Parse(id,CultureInfo.InvariantCulture),at,row.GetString(2),raw)):TimelineHash(raw)),at);
    }
    public MissionOperationsStatus OperationsStatus(Guid id)=>new(1,id,TimelineFlight(id).Status,ScienceDeleted(id),ScientificReview(id));
    public RecordedMissionOperations RecordedOperations(Guid id)
    {
        var m=Detail(id)?.Mission??throw new ArgumentException("Flight not found.");if(m.Status==MissionStatus.Recording)throw new InvalidOperationException("Use the shared live mission intelligence for the recording flight.");var state=CollectionCheckpoint(id);var deleted=ScienceDeleted(id);
        return new(1,m,Research(id),ResearchOperations(id),new(state,CollectionSessions(id),false,[],[],state?.CurrentSessionId is {} sid?CollectionSession(id,sid):null),deleted?[]:OperatingReports(id),ScientificReview(id),deleted,
            ["Read-only original archived state. Current aircraft telemetry is not attached to an earlier flight.","Saved outcomes and provisional processing remain separate from final verified scientific results and organization approval."]);
    }
    static T TimelineRead<T>(string raw,MissionTimelineReference reference)
    {if(Encoding.UTF8.GetByteCount(raw)>TimelineRowBytes)throw new NotSupportedException("This original record exceeds the bounded timeline inspection limit. Use its existing archive/export tools.");if(reference.Sha256!=TimelineHash(raw))throw new InvalidOperationException("The selected original evidence changed or was removed. Refresh its timeline; no substituted evidence was shown.");return JsonSerializer.Deserialize<T>(raw,WireJson.Options)??throw new InvalidOperationException("The original evidence is unavailable.");}
    ReplaySample[] TimelineTelemetry(Guid flight,DateTimeOffset from,DateTimeOffset to,long after,CancellationToken token)
    {
        using var db=Open();using var cmd=db.CreateCommand();cmd.CommandText="SELECT sequence,payload_json FROM telemetry WHERE mission_id=$flight AND timestamp_utc>=$from AND timestamp_utc<=$to AND sequence>$after ORDER BY timestamp_utc,sequence LIMIT 51";cmd.Parameters.AddWithValue("$flight",flight.ToString());cmd.Parameters.AddWithValue("$from",Stamp(from));cmd.Parameters.AddWithValue("$to",Stamp(to));cmd.Parameters.AddWithValue("$after",after);using var cancel=token.Register(cmd.Cancel);using var rows=cmd.ExecuteReader();var result=new List<ReplaySample>();while(rows.Read()){token.ThrowIfCancellationRequested();result.Add(new(rows.GetInt64(0),JsonSerializer.Deserialize<TelemetryFrame>(rows.GetString(1),WireJson.Options)!));}return result.ToArray();
    }
    CollectionEvent? TimelineCollectionAt(Guid mission,DateTimeOffset at)
    {
        using var db=Open();using var cmd=db.CreateCommand();cmd.CommandText=$"SELECT payload_json FROM collection_events WHERE mission_id=$id AND ({TimelineCollectionUtc})<=$utc ORDER BY ({TimelineCollectionUtc}) DESC,sequence DESC LIMIT 1";cmd.Parameters.AddWithValue("$id",mission.ToString());cmd.Parameters.AddWithValue("$utc",Stamp(at));return cmd.ExecuteScalar() is string raw?EvidenceContracts.Read<CollectionEvent>(raw):null;
    }
    internal CollectionEvent? TimelineCollectionSequence(Guid mission,long sequence)
    {using var db=Open();using var cmd=db.CreateCommand();cmd.CommandText="SELECT payload_json FROM collection_events WHERE mission_id=$id AND sequence=$sequence";cmd.Parameters.AddWithValue("$id",mission.ToString());cmd.Parameters.AddWithValue("$sequence",sequence);return cmd.ExecuteScalar() is string raw?EvidenceContracts.Read<CollectionEvent>(raw):null;}
    public MissionTimelineEvidence InspectTimeline(Guid flight,MissionTimelineReference reference,ScienceStore science,long afterSequence=0,CancellationToken token=default)
    {
        token.ThrowIfCancellationRequested();_=TimelineFlight(flight);if(!Enum.IsDefined(reference.Source)||reference.Id.Length>100||reference.Sha256.Length!=64||!reference.Sha256.All(Uri.IsHexDigit)||afterSequence<0)throw new ArgumentException("Select an exact original timeline reference.");var deleted=ScienceDeleted(flight);var warnings=new List<string>();var result=new MissionTimelineEvidence(1,flight,reference,DateTimeOffset.MinValue,"Original content hash matched","Exact original identity; adjacent records are time-window context only.");
        if(reference.Source==MissionTimelineSource.ScienceObservation){
            if(deleted)throw new InvalidOperationException("Collected science was permanently deleted. The timeline cannot recreate or substitute it.");var read=science.TimelineObservation(flight,reference,token);var o=read.Observation;result=result with{Observation=o,TimestampUtc=o.TimestampUtc};
            if(o.IntentId is {} intentId){var stored=ScienceIntent(intentId)??throw new InvalidOperationException("The exact original acquisition intent is missing.");var intent=stored.Intent;
                if(intent.MissionId!=flight||intent.SessionId!=o.SessionId||intent.InstrumentId!=o.InstrumentId||intent.ExposureUtc!=o.TimestampUtc)throw new InvalidOperationException("The original measurement and acquisition identities do not match.");
                var input=CollectionInput(flight,intent.InputReceiptId)??throw new InvalidOperationException("The original acquisition input was removed. No replacement is inferred.");var publication=science.SessionPublication(intent);
                if(publication is null||stored.ProductSha256 is null||publication.Value.Hash!=stored.ProductSha256)throw new InvalidOperationException("The retained original scientific publication no longer matches its acquisition receipt.");
                var source=TimelineCollectionSequence(flight,intent.CollectionEpoch);result=result with{Acquisition=stored,OriginalInput=input,Session=CollectionSession(flight,intent.SessionId),CollectionEvent=source,Integrity="Exact original observation, publication hash, intent, input and session verified"};
                if(o.Calibration is not null)warnings.Add("This observation includes a saved calibration derivation. Original input and derived/calibrated values remain distinct.");
            }else warnings.Add("Legacy observation has no acquisition intent/session reference. Original row identity is retained; no exact acquisition or session relationship is invented.");
            result=result with{ContextMeasurements=science.TimelineQuantityContext(flight,o,token)};
        }else{
            using var db=Open();using var cmd=db.CreateCommand();cmd.Parameters.AddWithValue("$flight",flight.ToString());var numeric=reference.Source is MissionTimelineSource.FlightEvent or MissionTimelineSource.CollectionEvent or MissionTimelineSource.Telemetry;
            if(numeric){if(!long.TryParse(reference.Id,out var number)||number<1)throw new ArgumentException("Choose an original numeric evidence identity.");cmd.Parameters.AddWithValue("$key",number);}else{if(!Guid.TryParse(reference.Id,out var id)||id==Guid.Empty)throw new ArgumentException("Choose an original evidence identity.");cmd.Parameters.AddWithValue("$key",id.ToString());}
            cmd.CommandText=reference.Source switch{
                MissionTimelineSource.FlightEvent=>"SELECT message FROM events WHERE mission_id=$flight AND id=$key",
                MissionTimelineSource.CollectionEvent=>"SELECT payload_json FROM collection_events WHERE mission_id=$flight AND sequence=$key",
                MissionTimelineSource.Telemetry=>"SELECT payload_json FROM telemetry WHERE mission_id=$flight AND sequence=$key",
                MissionTimelineSource.PilotMarker=>"SELECT payload_json FROM research_markers WHERE mission_id=$flight AND id=$key",
                MissionTimelineSource.ResearchNote=>"SELECT payload_json FROM research_notes WHERE mission_id=$flight AND id=$key",_=>throw new ArgumentException("Unsupported timeline source.")};
            using var cancel=token.Register(cmd.Cancel);var raw=cmd.ExecuteScalar() as string??throw new InvalidOperationException("The selected original was deleted or is unavailable. No replacement was generated.");
            if(Encoding.UTF8.GetByteCount(raw)>TimelineRowBytes)throw new NotSupportedException("This original exceeds the bounded timeline detail limit. Open its existing archive/export view.");if(reference.Source!=MissionTimelineSource.FlightEvent&&TimelineHash(raw)!=reference.Sha256)throw new InvalidOperationException("The selected original evidence changed. Refresh the timeline; no replacement was substituted.");
            switch(reference.Source){
                case MissionTimelineSource.FlightEvent:
                    cmd.CommandText="SELECT timestamp_utc,kind FROM events WHERE mission_id=$flight AND id=$key";using(var row=cmd.ExecuteReader()){row.Read();var e=new MissionEvent(long.Parse(reference.Id,CultureInfo.InvariantCulture),Parse(row.GetString(0)),row.GetString(1),raw);if(TimelineEventHash(e)!=reference.Sha256)throw new InvalidOperationException("The original event identity, time or content changed. Refresh the timeline.");result=result with{Event=e,TimestampUtc=e.TimestampUtc};if(e.Kind=="operating-assessment"){if(deleted)throw new InvalidOperationException("Scientific operating evidence was permanently deleted.");var operating=JsonSerializer.Deserialize<InstrumentOperatingReport>(raw,WireJson.Options)!;ValidateOperatingReport(operating);result=result with{Operating=operating,Session=operating.Assessment.SessionId is {} sid?CollectionSession(flight,sid):null};}}
                    cmd.CommandText="SELECT payload_json FROM personal_card_attempts WHERE mission_id=$flight AND (json_extract(payload_json,'$.beginEventId')=$key OR json_extract(payload_json,'$.endEventId')=$key) LIMIT 1";
                    if(cmd.ExecuteScalar() is string attempt){var a=JsonSerializer.Deserialize<RecordedResearchAttempt>(attempt,WireJson.Options)!;cmd.CommandText="SELECT card_json FROM personal_flight_cards WHERE mission_id=$flight AND card_id=$card";cmd.Parameters.AddWithValue("$card",a.CardId.ToString());var card=cmd.ExecuteScalar() as string;cmd.CommandText="SELECT payload_json FROM personal_research_attempts WHERE json_extract(payload_json,'$.recordedAttemptId')=$attempt LIMIT 1";cmd.Parameters.AddWithValue("$attempt",a.Id.ToString());var saved=cmd.ExecuteScalar() as string;result=result with{Attempt=a,Card=card is null?null:JsonSerializer.Deserialize<PersonalResearchCard>(card,WireJson.Options),SavedResult=saved is null?null:JsonSerializer.Deserialize<PersonalResearchAttempt>(saved,WireJson.Options)};}
                    break;
                case MissionTimelineSource.CollectionEvent:var collection=TimelineRead<CollectionEvent>(raw,reference);if(collection.MissionId!=flight)throw new InvalidOperationException("Collection evidence belongs to another flight.");result=result with{CollectionEvent=collection,Session=collection.SessionId is {} cs?CollectionSession(flight,cs):null,TimestampUtc=collection.InputUtc};break;
                case MissionTimelineSource.Telemetry:var frame=TimelineRead<TelemetryFrame>(raw,reference);result=result with{Telemetry=new(long.Parse(reference.Id,CultureInfo.InvariantCulture),frame),TimestampUtc=frame.TimestampUtc};break;
                case MissionTimelineSource.PilotMarker:var marker=TimelineRead<MissionMarker>(raw,reference);if(marker.MissionId!=flight)throw new InvalidOperationException("Pilot observation belongs to another flight.");result=result with{Marker=marker with{ScientificContextJson=null},TimestampUtc=marker.TimestampUtc};warnings.Add("Pilot Observation is written documentation, not a sensor measurement. Optional legacy scientific snapshots are not copied into this view.");break;
                case MissionTimelineSource.ResearchNote:var note=TimelineRead<MissionNote>(raw,reference);if(note.MissionId!=flight)throw new InvalidOperationException("Research note belongs to another flight.");result=result with{Note=note,TimestampUtc=note.TimestampUtc};break;
            }
        }
        token.ThrowIfCancellationRequested();if(result.Observation is not null&&ScienceDeleted(flight))throw new InvalidOperationException("Collected scientific evidence was deleted during inspection.");if(result.CollectionEvent is null){result=result with{CollectionEvent=TimelineCollectionAt(flight,result.TimestampUtc)};warnings.Add("Collection state is the last archived controller record at or before this UTC. It is contextual, not an invented causal acquisition link.");}
        var from=result.Attempt?.StartUtc??result.TimestampUtc.AddSeconds(-3);var to=result.Attempt?.EndUtc??(result.Attempt is null?result.TimestampUtc.AddSeconds(3):TimelineFlight(flight).EndedUtc??DateTimeOffset.UtcNow);if(result.Attempt is not null&&!deleted){try{result=result with{ContextMeasurements=science.TimelineObservations(flight,new(FromUtc:from,ToUtc:to,Limit:50),null,token).Take(50).ToArray()};warnings.Add("At most 50 scientific rows in this exact attempt window are contextual observations, not a claim that the card caused or requires each reading. Select a row for its original acquisition verification.");}catch(Exception e)when(e is IOException or SqliteException){warnings.Add("Scientific window context unavailable: "+e.Message);}}
        var samples=TimelineTelemetry(flight,from,to,afterSequence,token);var shown=samples.Take(50).ToArray();
        warnings.Add(result.Attempt is null?"Nearby flight samples are bounded contextual records from this flight, not an authoritative measurement join or interpolated position.":"Telemetry is paged from the exact original attempt interval. Saved outcomes remain distinct from a fresh evidence verification; use the existing result inspection/evaluation actions.");
        if(ScienceDeleted(flight)){
            var c=result.SavedResult?.Card;
            if(result.Observation is not null||result.Operating is not null||c is not null&&(c.AtmosphericProfile is not null||c.AtmosphericTransect is not null||c.Survey is not null||c.ObservationFlight is not null||c.ExperimentalSensor is not null))throw new InvalidOperationException("Collected scientific evidence was permanently deleted during inspection.");
            result=result with{ContextMeasurements=[]};warnings.Add("Collected science was permanently deleted. Original flight samples remain separate; no scientific values were reconstructed.");
        }
        return result with{ContextTelemetry=shown,NextTelemetrySequence=samples.Length>50?shown[^1].Sequence:null,Limitations=warnings.ToArray()};
    }
}
public sealed partial class ScienceStore
{
    internal MissionTimelinePointer TimelineObservationReference(Guid mission,string id)
    {if(!long.TryParse(id,out var n)||n<1)throw new ArgumentException("Choose a recorded observation identity.");using var db=Open();using var cmd=db.CreateCommand();cmd.CommandText="SELECT timestamp_utc,payload_json FROM observations WHERE mission_id=$mission AND id=$id";cmd.Parameters.AddWithValue("$mission",mission.ToString());cmd.Parameters.AddWithValue("$id",n);using var row=cmd.ExecuteReader();if(!row.Read())throw new InvalidOperationException("The original scientific observation is unavailable.");var raw=row.GetString(1);if(Encoding.UTF8.GetByteCount(raw)>MissionStore.TimelineRowBytes)throw new NotSupportedException("This original is too large for timeline inspection.");return new(new(MissionTimelineSource.ScienceObservation,n.ToString(CultureInfo.InvariantCulture),MissionStore.TimelineHash(raw)),DateTimeOffset.Parse(row.GetString(0),CultureInfo.InvariantCulture));}
    internal (ScienceObservation Observation,string Raw) TimelineObservation(Guid flight,MissionTimelineReference reference,CancellationToken token)
    {
        if(!long.TryParse(reference.Id,out var id)||id<1)throw new ArgumentException("Select an original observation identity.");if(AvailabilityError() is {} missing)throw new IOException(missing);using var db=Open();using var cmd=db.CreateCommand();cmd.CommandText="SELECT payload_json FROM observations WHERE mission_id=$flight AND id=$id";cmd.Parameters.AddWithValue("$flight",flight.ToString());cmd.Parameters.AddWithValue("$id",id);using var cancel=token.Register(cmd.Cancel);var raw=cmd.ExecuteScalar() as string??throw new InvalidOperationException("The original scientific observation is no longer available. It was not reconstructed.");if(Encoding.UTF8.GetByteCount(raw)>MissionStore.TimelineRowBytes||MissionStore.TimelineHash(raw)!=reference.Sha256)throw new InvalidOperationException("The selected original scientific observation changed. Refresh its timeline.");var row=ReadCatalog<ScienceObservation>(raw)with{Id=id};if(row.MissionId!=flight)throw new InvalidOperationException("The original observation flight identity differs.");return(row,raw);
    }
    internal MissionTimelineItem[] TimelineQuantityContext(Guid mission,ScienceObservation selected,CancellationToken token)
    {
        using var db=Open();using var cmd=db.CreateCommand();cmd.CommandText=$"SELECT id,{MissionStore.TimelineBoundedColumn("payload_json")} FROM observations WHERE dataset_id=$dataset AND mission_id=$mission AND measurement_id=$measurement AND unit=$unit AND timestamp_utc>=$from AND timestamp_utc<=$to AND json_extract(payload_json,'$.provenance')=$provenance AND json_extract(payload_json,'$.source')=$source ORDER BY timestamp_utc,id LIMIT 50";
        cmd.Parameters.AddWithValue("$dataset",selected.DatasetId.ToString());cmd.Parameters.AddWithValue("$mission",mission.ToString());cmd.Parameters.AddWithValue("$measurement",selected.MeasurementId);cmd.Parameters.AddWithValue("$unit",selected.Unit);cmd.Parameters.AddWithValue("$from",selected.TimestampUtc.AddSeconds(-15).ToString("O"));cmd.Parameters.AddWithValue("$to",selected.TimestampUtc.AddSeconds(15).ToString("O"));cmd.Parameters.AddWithValue("$provenance",JsonSerializer.Serialize(selected.Provenance,WireJson.Options).Trim('"'));cmd.Parameters.AddWithValue("$source",selected.Source);using var cancel=token.Register(cmd.Cancel);using var rows=cmd.ExecuteReader();var result=new List<MissionTimelineItem>();while(rows.Read()){
            token.ThrowIfCancellationRequested();var raw=MissionStore.TimelineOriginal(rows,1);var o=ReadCatalog<ScienceObservation>(raw)with{Id=rows.GetInt64(0)};if(o.Calibration?.DefinitionSha256!=selected.Calibration?.DefinitionSha256)continue;result.Add(new(new(MissionTimelineSource.ScienceObservation,o.Id.ToString(CultureInfo.InvariantCulture),MissionStore.TimelineHash(raw)),o.TimestampUtc,MissionTimelineLane.Measurements,o.MeasurementId+" observation","Bounded same-dataset/quantity/unit/provenance/source context; not interpolated",SessionId:o.SessionId,InstrumentId:o.InstrumentId,MeasurementId:o.MeasurementId,Value:double.IsFinite(o.Value)?o.Value:null,Unit:o.Unit,Quality:o.Quality,Provenance:o.Provenance,Latitude:o.Latitude,Longitude:o.Longitude,AltitudeMslFeet:o.AltitudeMslFeet));
        }return result.ToArray();
    }
}
