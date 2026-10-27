namespace AeroScience.Shared;

// Fictional mathematical response; never an actual hardware or simulator sensor.
public sealed record ExperimentalSensorDefinition(int Version=1,
    string Question="How does a fictional temperature sensor respond to recorded changes in ambient air temperature?",
    double ResponseSeconds=5,double Gain=1,double OffsetCelsius=0,
    double MinimumInputCelsius=-80,double MaximumInputCelsius=60,double SamplingSeconds=1,
    int MinimumSamples=20,double MinimumDurationSeconds=20,double MinimumInputSpanCelsius=.2,
    double MaximumTrackingErrorCelsius=5,double MaximumGapSeconds=3);
public sealed record ExperimentalSensorSummary(int Version,Guid LibraryId,long OriginalInputs,long ValidInputs,long ExcludedInputs,
    long ModeledSamples,long Segments,long Breaks,double SupportedSeconds,double? MinimumInputCelsius,double? MaximumInputCelsius,
    double? MaximumTrackingErrorCelsius,bool IntervalCovered,string[] Limitations);
// References original input rows; output is recomputed, never inserted as a measured observation.
public sealed record ExperimentalSensorPoint(long ObservationId,Guid DatasetId,Guid? SessionId,DateTimeOffset TimestampUtc,
    double InputCelsius,double TargetCelsius,double ModeledCelsius,double TrackingErrorCelsius,long Segment,
    string InputSource,ScienceProvenance InputProvenance,ObservationQuality InputQuality);
public sealed record ExperimentalSensorReview(bool MatchesSavedEvidence,ExperimentalSensorSummary Retained,
    ExperimentalSensorPoint[] Preview,bool Downsampled,string Message);
