using AeroScience.Shared;
namespace AeroScience.Missions;

public static class ProgramWorkflowRules
{
    public const int Version=1,MaximumActivePhases=16,MaximumRequirements=8;
    public static readonly string[] Limitations=[
        "Organization workflow for fictional/simulated testing only; not airworthiness, certification or operational safety.",
        "Original Pass, Fail and Insufficient Evidence remain immutable. Administrative dispositions never create scientific observations, P19 approved pilots or P20 sampling coverage.",
        "A waiver contributes no pilot. Partial contribution credit requires an actual received result and original supporting evidence, explicit manager authorization and a policy permitting that exact purpose.",
        "Campaign dispositions work without phases. Phase gates govern new claims/assignments only; existing claims, personal research, flight recording, science collection and simulator controls remain independent.",
        "Phase authorization requires current prerequisites and fulfillment. Holds, reopening, changed criteria or lost readiness invalidate release; prior decisions and their supporting references remain in history.",
        "Exceptions bind one exact publication, requirement and purpose. Same-card finding changes require explicit reauthorization. No exceptions transfer automatically to a new publication or phase definition.",
        "The initial hierarchy supports up to 16 active phases, eight requirements per phase and prerequisites strictly earlier in sequence. These processing bounds do not limit retained decision history."
    ];
    public static void Text(string? text,int maximum,string label)
    {if(string.IsNullOrWhiteSpace(text)||text.Length>maximum||text.Any(c=>char.IsControl(c)&&c is not('\n' or '\r' or '\t')))throw new ArgumentException($"{label} needs 1–{maximum} readable characters.");}
    public static void Validate(TestPhaseDefinition d)
    {
        if(d is null||d.Version!=1||d.Sequence is <1 or >1000000||!Enum.IsDefined(d.Advancement)||d.Prerequisites is null||d.Prerequisites.Length>16||d.Prerequisites.Any(id=>id==Guid.Empty)||d.Prerequisites.Distinct().Count()!=d.Prerequisites.Length||d.Requirements is not{Length:>=1 and <=MaximumRequirements})throw new ArgumentException("Choose the supported phase version, sequence, at most eight requirements and distinct earlier prerequisites.");
        Text(d.Name,120,"Phase name");Text(d.Description,2000,"Phase objective");
        if(d.Requirements.Any(r=>r is null||r.OpportunityId==Guid.Empty||r.PublicationRevision<1||!CampaignCoverageRules.Hash(r.PublicationSha256)||r.RequirementKey?.Length>100||r.RequirementKey is not null&&string.IsNullOrWhiteSpace(r.RequirementKey)||r.RequiredPilots is <1 or >1000||r.CountAuthorizedPartialContributors&&!r.AllowExceptions)||d.Requirements.Select(r=>(r.OpportunityId,r.RequirementKey)).Distinct().Count()!=d.Requirements.Length)throw new ArgumentException("Choose exact published card/requirement identities and 1–1,000 distinct contributors. Partial credit needs an explicit exception policy.");
    }
    public static void Graph(IEnumerable<TestPhaseSnapshot> snapshots)
    {
        var phases=snapshots.Where(p=>p.Definition.Enabled).ToArray();if(phases.Length>MaximumActivePhases||phases.Select(p=>p.Definition.Sequence).Distinct().Count()!=phases.Length)throw new ArgumentException("Use at most 16 active phases with unique sequence numbers.");
        foreach(var p in phases){Validate(p.Definition);if(p.Definition.Prerequisites.Any(id=>!phases.Any(q=>q.Id==id&&q.Definition.Sequence<p.Definition.Sequence)))throw new ArgumentException("Prerequisites must be enabled phases earlier in sequence; cycles and missing prerequisites are not allowed.");}
        if(phases.SelectMany(p=>p.Definition.Requirements.Select(r=>new{p.Id,r.OpportunityId})).GroupBy(r=>r.OpportunityId).Any(g=>g.Select(v=>v.Id).Distinct().Count()>1))throw new ArgumentException("Link each card to one active phase. Multiple named requirements from that card may share its phase.");
    }
}
