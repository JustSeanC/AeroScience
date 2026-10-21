using AeroScience.Shared;
namespace AeroScience.Missions;

public sealed record AcquisitionResult(Guid IntentId,Guid SessionId,string InstrumentId,DateTimeOffset ExposureUtc,
    int ExpectedMeasurements,int ValidMeasurements,int MissingMeasurements,bool Assessed,bool Acquired,bool Reconstructed,
    string[] Evidence,string[] Limitations);

public static class ScienceYieldEvaluation
{
    public static ScienceYield Calculate(Guid mission,Guid? session,string instrument,DateTimeOffset at,IEnumerable<AcquisitionResult> evidence)
    {
        // Session IDs attribute evidence only; flight totals deduplicate the original intent.
        var rows=evidence.Where(r=>r.InstrumentId==instrument&&(!session.HasValue||r.SessionId==session)&&r.ExposureUtc<=at)
            .GroupBy(r=>r.IntentId).Select(g=>g.Distinct().Count()==1?g.First():g.All(r=>r==g.First())?g.First():throw new InvalidDataException("Conflicting duplicate acquisition evidence.")).ToArray();
        if(rows.Any(r=>r.ExpectedMeasurements<0||r.ValidMeasurements<0||r.ValidMeasurements>r.ExpectedMeasurements||r.MissingMeasurements<0||r.MissingMeasurements>r.ExpectedMeasurements))throw new InvalidDataException("Invalid measurement accounting.");
        var live=rows.Where(r=>!r.Reconstructed).ToArray();var assessed=live.Where(r=>r.Assessed).ToArray();
        ScienceMetric Fraction(string id,double numerator,double denominator,string unit,string method,long missing=0,long unassessed=0)=>new(id,denominator>0?AssessmentStatus.Supported:AssessmentStatus.NotAssessed,denominator>0?numerator:null,denominator>0?denominator:null,unit,method,missing,unassessed,EvidenceOrigin.Derived,[]);
        var metrics=new[]{
            Fraction("good-data-sets",assessed.Count(r=>r.Acquired&&r.ValidMeasurements>0&&r.MissingMeasurements==0),assessed.Length,"acquisition sets","Complete QA-valid original acquisition sets / assessed actual acquisition opportunities; not recorded flight samples.",assessed.Count(r=>r.MissingMeasurements>0),live.Count(r=>!r.Assessed)),
            Fraction("good-data-values",assessed.Sum(r=>r.ValidMeasurements),assessed.Sum(r=>r.ExpectedMeasurements),"original measurement values","Distinct original measurement channels; wind speed/direction are two values in one set. User-calibration derivatives are excluded.",assessed.Sum(r=>r.MissingMeasurements),live.Count(r=>!r.Assessed)),
            Fraction("acquisition-success",assessed.Count(r=>r.Acquired&&r.ValidMeasurements>0),assessed.Length,"acquisition sets","Actual original results with at least one valid scientific value / assessed opportunities; provider failures never imply pilot noncompliance.",assessed.Count(r=>!r.Acquired),live.Count(r=>!r.Assessed)),
            new ScienceMetric("reconstructed-sets",AssessmentStatus.Supported,rows.Count(r=>r.Reconstructed),null,"acquisition sets","Separately labeled later reconstructed/downlinked results; excluded from flight-time Good Data and performance credit.",0,0,EvidenceOrigin.Derived,[])
        };
        var result=new ScienceYield(1,mission,session,instrument,at,metrics,["Scientific yield is distinct from operating compliance and pilot performance. Pending/unassessed results remain explicit; no historical observation is invented."]);
        EvidenceContracts.Validate(result);return result;
    }
}
