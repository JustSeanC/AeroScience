using System.Text.Json;
using AeroScience.Missions;
using AeroScience.Shared;

// Test-only subprocess protocol. Not mapped to HTTP and never distributed with AeroScience.
internal static class EvidenceProbe
{
    internal static void Run(string[] args)
    {
        var store = new MissionStore(args[1]); store.Initialize();
        if (args[0] == "inspect")
        {
            var mission = store.List().Single(); var before = store.CollectionCheckpoint(mission.Id)!;
            var count = store.CollectionSessions(mission.Id).Length;
            store.RecoverInterrupted(); var after = store.CollectionCheckpoint(mission.Id)!;
            Console.WriteLine(JsonSerializer.Serialize(new { before, after, sessions = store.CollectionSessions(mission.Id),
                beforeSessionCount = count, mission = store.Detail(mission.Id)!.Mission, samples = store.StreamSamples(mission.Id).Count(),
                events = store.CollectionEvents(mission.Id).Length, intents = store.ScienceIntents(mission.Id) }, WireJson.Options));
            return;
        }
        var utc = new DateTimeOffset(2026, 10, 20, 12, 0, 0, TimeSpan.Zero);
        var flight = store.Create("SYNTHETIC actual-process evidence fixture", null, utc);
        var initial = new CollectionState(1, flight, 0, CollectionPhase.Armed, CollectionAuthority.Manual);
        store.InitializeCollectionEvidence(initial); var sessionId = Guid.NewGuid(); var configId = Guid.NewGuid();
        var frame = new TelemetryFrame(utc, TelemetrySource.Simulated, "Synthetic process fixture", 40, -75, 1000, 500, 100, 110, 110, 0, 0, 0, 0, 0);
        var packet = new CollectionInputReceipt("exact-input", new(new(ConnectionState.Connected, TelemetrySource.Simulated, "Synthetic fixture", utc), frame));
        var session = new ScienceCollectionSession(1, sessionId, flight, 1, utc, CollectionSessionStatus.Collecting,
            [new(configId, 1, utc, new("ambient-temperature", true))], [MissionStore.DescribeCollectionInput(packet)]);
        var next = initial with { Revision = 1, Phase = CollectionPhase.Collecting, CurrentSessionId = sessionId };
        var request = new CollectionControlRequest(1, flight, Guid.NewGuid(), 0, CollectionAction.StartData, null);
        var change = new CollectionEvent(1, flight, 1, utc, utc, sessionId, CollectionAction.StartData, CollectionEventOrigin.Manual,
            CollectionEventOutcome.Applied, initial.Phase, next.Phase, next.Authority, "Synthetic start", session.Evidence, RequestId: request.RequestId);
        var commit = new CollectionEvidenceCommit(next, change, session, request, [packet],
            [new(Guid.NewGuid(), flight, sessionId, "ambient-temperature", 0, configId, 1, utc, packet.Id)], frame);
        if (args[0] == "close")
        {
            store.CommitCollectionEvidence(commit);
            var stopped = next with { Revision = 2, Phase = CollectionPhase.BetweenSessions, CurrentSessionId = null, LastSessionId = sessionId, ManualAutomationHold = true };
            var endRequest = new CollectionControlRequest(1, flight, Guid.NewGuid(), 1, CollectionAction.EndData, sessionId);
            commit = new(stopped, change with { Sequence = 2, InputUtc = utc.AddSeconds(2), RecordedUtc = utc.AddSeconds(2), Action = CollectionAction.EndData,
                From = next.Phase, To = stopped.Phase, RequestId = endRequest.RequestId, ExpectedSessionId = sessionId, Evidence = [] },
                session with { Status = CollectionSessionStatus.Ended, EndReason = CollectionEndReason.Manual, EndedUtc = utc.AddSeconds(2) }, endRequest);
        }
        store.EvidenceFault = point =>
        {
            if (point != args[3]) return;
            File.WriteAllText(args[2], JsonSerializer.Serialize(new { flight, sessionId }));
            Thread.Sleep(Timeout.Infinite); // Driver kills the OS process at the exact durable boundary.
        };
        store.CommitCollectionEvidence(commit);
        throw new InvalidOperationException("The requested test kill point was not reached.");
    }
}
