using AeroScience.Shared;

namespace AeroScience.Missions;

// Small card-specific extension: the existing mission rubric grades different obligations.
// No interpolation, accumulated disjoint holds, source conversion, or science grading changes.
public static class FlightRegimeEvaluation
{
    public const string Version = "flight-regime-1";
    public const double MaximumGapSeconds = 1.5; // supports the existing 1 Hz recorder

    public static void Validate(FlightRegimeDefinition? rule)
    {
        if (rule is null || !new[] { rule.AirspeedKnots, rule.AirspeedToleranceKnots,
            rule.AltitudeMslFeet, rule.AltitudeToleranceFeet, rule.HoldSeconds }.All(double.IsFinite)
            || rule.AirspeedKnots is < 1 or > 2000 || rule.AirspeedToleranceKnots <= 0
            || rule.AirspeedToleranceKnots >= rule.AirspeedKnots
            || rule.AltitudeMslFeet is < -2000 or > 100000
            || rule.AltitudeToleranceFeet is <= 0 or > 10000 || rule.HoldSeconds is < 1 or > 3600)
            throw new ArgumentException("Enter a supported indicated airspeed (knots), MSL altitude (feet), positive tolerances and a hold of 1–3600 seconds.");
    }

    public sealed class Window
    {
        private readonly FlightRegimeDefinition rule;
        private readonly DateTimeOffset start, end;
        private DateTimeOffset? previous, continuousStart, holdStart;
        private TelemetrySource? previousSource;
        private string? aircraft;
        private bool incomplete;
        private double observed;
        public double LongestHold { get; private set; }
        public DateTimeOffset? HoldStart { get; private set; }
        public DateTimeOffset? HoldEnd { get; private set; }

        public Window(FlightRegimeDefinition rule, DateTimeOffset start, DateTimeOffset end)
        { Validate(rule); this.rule = rule; this.start = start; this.end = end; }

        public void Break()
        { incomplete = true; previous = continuousStart = holdStart = null; }

        public void Add(TelemetryFrame sample)
        {
            var at = sample.TimestampUtc;
            if (at < start || at > end) return;
            if (!sample.IsValid || sample.IsPaused || !Enum.IsDefined(sample.Source)
                || sample.IndicatedAirspeedKnots < 0)
            { Break(); return; }
            if (previous is null && (at - start).TotalSeconds > MaximumGapSeconds) incomplete = true;
            if (previous is {} prior && ((at - prior).TotalSeconds is <= 0 or > MaximumGapSeconds
                || previousSource != sample.Source || aircraft != sample.Aircraft)) Break();
            continuousStart ??= at;
            observed = Math.Max(observed, (at - continuousStart.Value).TotalSeconds);
            if (Math.Abs(sample.IndicatedAirspeedKnots - rule.AirspeedKnots) <= rule.AirspeedToleranceKnots
                && Math.Abs(sample.AltitudeMslFeet - rule.AltitudeMslFeet) <= rule.AltitudeToleranceFeet)
            {
                holdStart ??= at;
                var duration = (at - holdStart.Value).TotalSeconds;
                if (duration > LongestHold)
                { LongestHold = duration; HoldStart = holdStart; HoldEnd = at; }
            }
            else holdStart = null;
            previous = at; previousSource = sample.Source; aircraft = sample.Aircraft;
        }

        public (ResearchAttemptOutcome Outcome, string Explanation) Finish(bool interrupted)
        {
            if (LongestHold >= rule.HoldSeconds)
                return (ResearchAttemptOutcome.Pass, "Recorded indicated airspeed and MSL altitude stayed inside both tolerances for the required continuous hold. This is a simulator test, not aircraft certification.");
            if (interrupted || incomplete || previous is null || (end - previous.Value).TotalSeconds > MaximumGapSeconds
                || observed < rule.HoldSeconds)
                return (ResearchAttemptOutcome.InsufficientEvidence, "The selected interval cannot establish the required hold. Missing or invalid samples, simulator pauses, recording gaps, changed aircraft/source or an interrupted recording are not counted as success.");
            return (ResearchAttemptOutcome.Fail, "The recorded interval is long enough to assess, but no continuous hold met both the indicated-airspeed and MSL-altitude tolerances.");
        }
    }
}
