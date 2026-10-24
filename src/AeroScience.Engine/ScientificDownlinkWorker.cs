using AeroScience.Missions;
using AeroScience.Shared;
using System.Text.Json;
namespace AeroScience.Engine;

// One bounded background reconstruction at a time, never on the 1 Hz recorder gate.
public sealed class ScientificDownlinkWorker(MissionStore missions, ScienceStore science, AutomaticScientificData automatic,
    ScientificAccessSettings settings, MissionCoordinator coordinator, ScientificDownlinkSignals signals, ILogger<ScientificDownlinkWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var sampling = new RemoteAcquisition(missions, science, () => settings.Get().Preferences);
        var reconciliationOffset=0;
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(2));
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            if (coordinator.Snapshot().ActiveMission is not null) continue;
            try
            {
                var scanned=missions.ReconcileSessionPublications(science,reconciliationOffset);
                reconciliationOffset=scanned<32?0:reconciliationOffset+scanned;
                var rows = missions.Recovery(limit: 24, pendingOnly: true);
                foreach (var slot in rows)
                {
                    stoppingToken.ThrowIfCancellationRequested();
                    if (missions.Detail(slot.MissionId)?.Mission.Status == MissionStatus.Recording) continue;
                    if (missions.RecoveryById(slot.Id) is { State: "Paused" or "Cancelled" }) continue;
                    if (automatic.AvailabilityError is {} storageError) { missions.UpdateRecovery(slot with { State = "Blocked", Reason = "Scientific storage unavailable: " + storageError + ". Pending evidence and originals are preserved." }); continue; }
                    using var operation = signals.Begin(slot.MissionId, stoppingToken, out var operationToken);
                    using var lease = science.BeginAnalysis();
                    if (!missions.VerifyRecoveryEvidence(slot)) { missions.UpdateRecovery(slot with { State = "Gap", Reason = "Exact archived telemetry/configuration evidence unavailable or intentionally paused. Original flight preserved." }); continue; }
                    var area = GeographicBounds.Around(new(slot.Evidence.Frame.Latitude, slot.Evidence.Frame.Longitude));
                    var kind = RemoteAcquisition.Instruments[slot.InstrumentId]; var source = ScientificSourceSelection.Select(science.Availability(area.Center,kind), kind, settings.Get().Preferences, slot.Evidence.Configuration.Payload);
                    if (source is null)
                    {
                        var scope = "plan:downlink:" + slot.MissionId;
                        automatic.Prepare(area, [kind], background: true, scope: scope, samplePoint: area.Center);
                        var preparation = automatic.Snapshot().FirstOrDefault(r => r.Key == AutomaticScientificData.AreaKey(kind, area));
                        var blocked = preparation?.Status is "Unavailable" or "Offline" or "Limited Coverage" or "Waiting for download allowance" or "Dataset Required" or "Dataset Outside Coverage";
                        missions.UpdateRecovery(slot with { State = blocked ? "Blocked" : preparation is {Stage:"Downloading",DownloadedBytes:>0} ? "Downloading" : "Pending", Reason = preparation?.Message ?? "Waiting for bounded scientific preparation; transfer ETA unknown." }); continue;
                    }
                    missions.UpdateRecovery(slot with { State = "Reconstructing", Reason = "Verified source available; sampling archived packet/configuration." });
                    try
                    {
                        var product = await Task.Run(() => sampling.Reconstruct(slot, operationToken), operationToken);
                        var recovered = slot with { State = "Recovered", Reason = "Post-flight derived reconstruction; rebuilding bounded derived scientific results.", ProductId = product.Id };
                        missions.UpdateRecovery(recovered);
                        string? limitations = null;
                        try
                        {
                            await Task.Run(() =>
                            {
                                var data = science.ReadProduct(product.Id);
                                var measurement = kind switch { RemoteKind.Terrain => "elevation", RemoteKind.Thermal => "temperature", RemoteKind.Multispectral => "ndvi", _ => data.Bands.First(b => b.Values.Any(v => v.HasValue)).Id };
                                _ = ScientificComposites.Build(science, missions, slot.MissionId, new(product.SourceId, measurement), operationToken);
                                var coverage = ScientificCoverage.Calculate(science, missions.Research(slot.MissionId).PlanSnapshot, slot.MissionId, new(slot.InstrumentId, measurement), operationToken);
                                var bytes = JsonSerializer.SerializeToUtf8Bytes(coverage, WireJson.Options);
                                _ = science.PublishArtifact(slot.MissionId, "coverage", "json", ScientificComposites.Identity(new { coverage, algorithm = "scientific-coverage-1" }), new { coverage.InstrumentId, coverage.Acquisitions, coverage.ValidDataSurveyPercent }, Math.Max(1048576, bytes.LongLength + 65536), path => File.WriteAllBytes(path, bytes), operationToken);
                            }, operationToken);
                        }
                        catch (Exception e) when (e is InvalidOperationException or ArgumentException) { limitations = e.Message; }
                        missions.UpdateRecovery(recovered with { DerivedCompleted = true, DerivedLimitations = limitations, Reason = limitations is null ? "Recovered and derived geographic results refreshed. Historical source time, flight time and processing time remain distinct." : "Recovered measurement preserved; derived analysis is limited: " + limitations });
                    }
                    catch (OperationCanceledException) when (!stoppingToken.IsCancellationRequested) {/* Persisted pause/cancel remains authoritative. */}
                    catch (ScientificObservationUnavailableException e) { missions.UpdateRecovery(slot with { State = "Gap", Reason = e.Message }); }
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception error) when (error is not OutOfMemoryException) { logger.LogWarning("Scientific downlink waiting: {Reason}. Originals preserved; recorder remains independent.", error.Message); }
        }
    }
}
