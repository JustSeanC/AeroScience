using System.Collections.Concurrent;
using AeroScience.Missions;
using AeroScience.Shared;
namespace AeroScience.Engine;

// Background interpretation/delivery of expressly approved references only. Never on the 1 Hz recorder path.
public sealed class SubmissionDelivery(MissionStore store,ScienceStore science,OrganizationConnections connections,MissionCoordinator coordinator):IDisposable
{
    readonly SemaphoreSlim gate=new(1,1);
    readonly ConcurrentDictionary<Guid,CancellationTokenSource> active=[];
    public async Task<SubmissionDraft> Send(Guid id,CancellationToken ct)
    {
        var start=store.SubmissionDraft(id);if(start.State is not(SubmissionDeliveryState.ApprovedQueued or SubmissionDeliveryState.RetryAvailable)||start.Consent is null)return start;
        if(coordinator.Snapshot().ActiveMission is not null)return start; // No new work during recording; approval does not control a flight.
        if(!await gate.WaitAsync(0,ct))return start;
        using var transfer=CancellationTokenSource.CreateLinkedTokenSource(ct);active[id]=transfer;bool readingOriginals=false;
        using var monitorStop=new CancellationTokenSource();var priority=MonitorFlight(transfer,monitorStop.Token);
        try{
            var remote=await connections.SubmissionStatus(start,transfer.Token);
            if(remote.Status is not("Received" or "Not Received" or "Cancelled")||remote.Status=="Received"&&remote.Receipt is null)throw new OrganizationSubmissionReviewRequired("The server returned an unsupported receipt status. No confirmed outcome was invented.");
            if(remote.Receipt is {} retained)return RecordReceipt(id,retained); // Lost acknowledgment, even if local originals were later deleted.
            if(remote.Status=="Cancelled")return store.ChangeSubmission(id,start.Revision,r=>r with{State=SubmissionDeliveryState.Cancelled,NextAttemptUtc=null,Message="Cancellation confirmed by the organization. No evidence was received under this identity."});
            await connections.RefreshSubmissionPolicies(start.ConnectionId!.Value,start,store,transfer.Token);
            if(coordinator.Snapshot().ActiveMission is not null)return start;
            // Read leases prevent scientific deletion from reporting completion while approved original bytes are in flight.
            readingOriginals=true;
            using var originals=start.Manifest.Items.Any(i=>i.Kind is SubmissionEvidenceKind.ScientificObservations or SubmissionEvidenceKind.PilotDocumentation)&&science.AvailabilityError() is null?science.BeginAnalysis():null;
            var document=store.ApprovedSubmissionDocument(id,science,transfer.Token);
            readingOriginals=false;
            var sending=store.ChangeSubmission(id,start.Revision,r=>r with{State=SubmissionDeliveryState.Sending,DeliveryAttempts=r.DeliveryAttempts+1,NextAttemptUtc=null,Message="Sending the exact explicitly approved package. Flight recording and scientific collection remain independent."});
            var receipt=await connections.SendSubmission(sending,document,transfer.Token);return RecordReceipt(id,receipt);
        }catch(Exception e) when(e is IOException or UnauthorizedAccessException or HttpRequestException or OperationCanceledException or ArgumentException or InvalidOperationException){
            var row=store.SubmissionDraft(id);if(row.State is SubmissionDeliveryState.Cancelled or SubmissionDeliveryState.ServerReceived)return row;
            if(e is OperationCanceledException&&coordinator.Snapshot().ActiveMission is not null)return store.ChangeSubmission(id,row.Revision,r=>r with{State=SubmissionDeliveryState.ApprovedQueued,NextAttemptUtc=DateTimeOffset.UtcNow.AddSeconds(5),Message="Approved delivery paused for flight-recording priority. Originals and receipt identity will be rechecked after the flight."});
            if(e is OrganizationSubmissionAuthorizationRequired or UnauthorizedAccessException)return store.ChangeSubmission(id,row.Revision,r=>r with{State=SubmissionDeliveryState.HeldAuthorization,NextAttemptUtc=null,Message="Held — Authorization Required. Reconnect with Submit Evidence permission, then review and approve a new package. Nothing was confirmed received."});
            if(e is OrganizationSubmissionReviewRequired)return store.ChangeSubmission(id,row.Revision,r=>r with{State=SubmissionDeliveryState.HeldEvidenceMissing,NextAttemptUtc=null,Message="Held — Review Required. The server could not receive this exact policy/claim/package. Refresh its requirements and review a new approval; no receipt was confirmed."});
            if(readingOriginals||e is ArgumentException or InvalidOperationException)return store.ChangeSubmission(id,row.Revision,r=>r with{State=SubmissionDeliveryState.HeldEvidenceMissing,NextAttemptUtc=null,Message="Held — Evidence Missing or Changed. Original evidence, selection or policy no longer matches approval. No replacement or reconstructed science was sent. Review the original sources and a new draft."});
            var attempts=Math.Max(row.DeliveryAttempts,start.DeliveryAttempts+1);return store.ChangeSubmission(id,row.Revision,r=>r with{State=SubmissionDeliveryState.RetryAvailable,DeliveryAttempts=attempts,NextAttemptUtc=attempts<3?DateTimeOffset.UtcNow.AddSeconds(10*Math.Pow(2,attempts)):null,Message=attempts<3?"Retry Available. The organization could not confirm delivery; the same approved identity may retry after bounded backoff. A receipt lookup precedes any new byte transfer.":"Retry Available. Automatic retries stopped after three attempts. Check the connection and deliberately choose Retry; no duplicate receipt or replacement evidence was invented."});
        }finally{monitorStop.Cancel();try{await priority;}catch(OperationCanceledException){}active.TryRemove(id,out _);gate.Release();}
    }
    SubmissionDraft RecordReceipt(Guid id,SubmissionReceipt receipt)
    {try{return store.RecordSubmissionReceipt(id,receipt);}catch(IOException){throw new OrganizationSubmissionReviewRequired("The server receipt does not match this exact approval. Delivery is held for review; no confirmed outcome was invented.");}}
    async Task MonitorFlight(CancellationTokenSource transfer,CancellationToken ct)
    {using var timer=new PeriodicTimer(TimeSpan.FromMilliseconds(250));while(await timer.WaitForNextTickAsync(ct))if(coordinator.Snapshot().ActiveMission is not null){transfer.Cancel();return;}}
    public async Task<SubmissionDraft> Cancel(Guid id,int revision,CancellationToken ct)
    {
        var row=store.CancelSubmission(id,revision);if(active.TryGetValue(id,out var current))current.Cancel();
        if(row.Consent is null)return row;
        try{var remote=await connections.CancelSubmission(row,ct);if(remote.Receipt is {} receipt)return RecordReceipt(id,receipt);var latest=store.SubmissionDraft(id);if(latest.State==SubmissionDeliveryState.ServerReceived)return latest;return store.ChangeSubmission(id,latest.Revision,r=>r with{Message="Cancelled. The organization confirmed this submission cannot be received. Local original research remains unchanged."});}
        catch(Exception e) when(e is IOException or UnauthorizedAccessException or HttpRequestException or OperationCanceledException){if(ct.IsCancellationRequested)throw;return store.SubmissionDraft(id);}
    }
    public void Dispose(){foreach(var source in active.Values)source.Cancel();gate.Dispose();}
}

public sealed class SubmissionDeliveryWorker(SubmissionDelivery delivery,MissionStore store,MissionCoordinator coordinator,ILogger<SubmissionDeliveryWorker> log):BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while(store.RecoverSubmissionDeliveries()==20){stoppingToken.ThrowIfCancellationRequested();await Task.Yield();}using var timer=new PeriodicTimer(TimeSpan.FromSeconds(5));
        while(!stoppingToken.IsCancellationRequested){
            try{if(coordinator.Snapshot().ActiveMission is null&&store.NextApprovedSubmission() is {} next)await delivery.Send(next.Id,stoppingToken);}
            catch(OperationCanceledException) when(stoppingToken.IsCancellationRequested){break;}
            catch(Exception ex){log.LogWarning("Approved submission delivery paused ({FailureType}); no evidence or credentials logged.",ex.GetType().Name);}
            if(!await timer.WaitForNextTickAsync(stoppingToken))break;
        }
    }
}
