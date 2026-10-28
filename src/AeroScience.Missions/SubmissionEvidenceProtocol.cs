using System.Security.Cryptography;
using System.Text.Json;
using AeroScience.Shared;
namespace AeroScience.Missions;

// A bounded transport interpretation of existing archives, not a recorder or scientific approval.
public static class SubmissionEvidenceProtocol
{
    public const int Version=1,MaximumPayloadBytes=2*1024*1024,MaximumWireBytes=MaximumPayloadBytes+8192,MaximumItems=512,MaximumSamples=10000;
    public const string HistoryDisclosure="This is a pilot-selected subset. Unshared failed, interrupted or insufficient attempts may exist. It does not establish that omitted evidence did not exist or that attempts are independent.";
    public const string PrivacyDisclosure="Only selected evidence is shared. Selected telemetry includes original locations, timestamps, aircraft identity and recorded channels; selected notes include their original text and available location. An authorized received server copy is independent of local deletion. No credentials, unrelated organization cards, private unselected notes or external scene files are included.";
    public const string RetentionDisclosure="The organization retains the expressly submitted evidence in its protected PostgreSQL database. P17 has no remote deletion or automatic retention expiry. Deleting local science does not remove received remote evidence. Production retention, backups and restore require separate deployment acceptance.";
    public const string MinimumRule="evidence-minimum-1: supported original quantitative evidence must be assessable (a measured Fail is allowed); atmospheric/pilot documentation uses the issued collection goal. Unsupported aggregate/product completeness requires Allow Incomplete Submission and manual review. Receipt is not scientific approval.";
    public static string Hash(object value)=>PreflightReadiness.Hash(value);
    public static int Bytes(object value)=>JsonSerializer.SerializeToUtf8Bytes(value,WireJson.Options).Length;
    public static PersonalResearchCard ScopedCard(PersonalResearchCard card)=>card with{OrganizationAssignment=null};
    public static PersonalResearchAttempt ScopedResult(PersonalResearchAttempt result)=>result with{Card=ScopedCard(result.Card)};
    static bool Digest(string? value)=>value is {Length:64}&&value.All(char.IsAsciiHexDigit);
    public static SubmissionPolicy DefaultPolicy(Guid opportunity,DateTimeOffset at)=>new(1,opportunity,1,SubmissionPolicyMode.RequireMinimumEvidence,MinimumRule,MaximumPayloadBytes,at,RetentionDisclosure);
    public static void Validate(SubmissionDocument document,CancellationToken ct=default)
    {
        if(document is null)throw new ArgumentException("Choose a supported evidence document.");
        var m=document.Manifest;
        if(m is null||m.Version!=1||m.Id==Guid.Empty||m.OrganizationId==Guid.Empty||m.PilotId==Guid.Empty||m.FlightId==Guid.Empty||m.CampaignId==Guid.Empty
            ||string.IsNullOrWhiteSpace(m.Name)||m.Name.Length>120||m.Name.Any(char.IsControl)||m.OrganizationName is not {Length:>=1 and <=120}||m.CampaignName is not {Length:>=1 and <=200}
            ||!Uri.TryCreate(m.Origin,UriKind.Absolute,out var origin)||origin.UserInfo!=""||origin.Query!=""||origin.Fragment!=""||origin.AbsolutePath!="/"
            ||m.ResultIds is not {Length:>=1 and <=16}||m.ResultIds.Any(id=>id==Guid.Empty)||m.ResultIds.Distinct().Count()!=m.ResultIds.Length
            ||m.Cards is not {Length:>=1 and <=4}||m.Cards.Any(c=>c is null)||m.Cards.Select(c=>c.CardId).Distinct().Count()!=m.Cards.Length||m.Cards.Select(c=>c.ClaimId).Distinct().Count()!=m.Cards.Length
            ||m.Policies is null||m.Policies.Length!=m.Cards.Length||m.Policies.Any(p=>p is null)||m.Policies.Select(p=>p.OpportunityId).Distinct().Count()!=m.Policies.Length||m.Items is not {Length:>=1 and <=MaximumItems}||m.Items.Any(i=>i is null)||m.Items.Select(i=>i.Key).Distinct().Count()!=m.Items.Length
            ||document.Evidence is null||document.Evidence.Length!=m.Items.Length||document.Evidence.Any(i=>i is null||i.Reference is null||i.Data is null)||document.Evidence.Select(i=>i.Reference.Key).Distinct().Count()!=document.Evidence.Length||m.HistoryDisclosure!=HistoryDisclosure||m.PrivacyDisclosure!=PrivacyDisclosure)
            throw new ArgumentException("The submission has an unsupported identity, scope, size or disclosure. Review the selected evidence again.");
        if(m.ReviewLink is {} link&&(link.Version!=1||link.RootReceiptId==Guid.Empty||link.RequestEventId==Guid.Empty||!Digest(link.RootPayloadSha256)))throw new ArgumentException("Choose an exact original review/request identity for supplemental evidence.");
        foreach(var card in m.Cards){
            ct.ThrowIfCancellationRequested();if(card.ClaimId==Guid.Empty||card.OpportunityId==Guid.Empty||card.CampaignId!=m.CampaignId||card.CardId==Guid.Empty||card.CardRevision<1||card.IssuedRevision<1||!Digest(card.IssuedSha256)||!Digest(card.DefinitionSha256)
                ||card.StartUtc.Offset!=TimeSpan.Zero||card.EndUtc.Offset!=TimeSpan.Zero||card.EndUtc<card.StartUtc||card.EndUtc-card.StartUtc>TimeSpan.FromHours(24)||card.KnownRecordedAttempts<0)
                throw new ArgumentException("A selected claim, original definition or recorded interval is invalid.");
            var p=m.Policies.SingleOrDefault(p=>p.OpportunityId==card.OpportunityId);
            if(p is null||p.Version!=1||p.Revision<1||!Enum.IsDefined(p.Mode)||p.MinimumRule!=MinimumRule||p.MaximumPayloadBytes!=MaximumPayloadBytes||p.RetentionDisclosure!=RetentionDisclosure)
                throw new ArgumentException("Review a supported published submission policy before approval.");
        }
        foreach(var item in document.Evidence){
            ct.ThrowIfCancellationRequested();var r=item.Reference;var d=item.Data;
            if(r is null||d is null||!m.Items.Any(i=>i.Key==r.Key&&Hash(i)==Hash(r))||r.Key is not {Length:>=1 and <=160}||r.FlightId!=m.FlightId||!m.Cards.Any(c=>c.CardId==r.CardId)
                ||!Digest(r.SourceSha256)||!Digest(r.ContentSha256)||Hash(d)!=r.ContentSha256||Bytes(d)!=r.ContentBytes||r.ContentBytes>MaximumPayloadBytes||r.Records<0
                ||r.Description is not {Length:>=1 and <=300}||r.Privacy is not {Length:>=1 and <=1000}||r.StartUtc.Offset!=TimeSpan.Zero||r.EndUtc.Offset!=TimeSpan.Zero||r.EndUtc<r.StartUtc)
                throw new ArgumentException("An evidence item differs from the exact reviewed manifest.");
            var binding=m.Cards.Single(c=>c.CardId==r.CardId);
            if(r.StartUtc<binding.StartUtc||r.EndUtc>binding.EndUtc)throw new ArgumentException("Evidence crosses the selected claim's recorded interval.");
            if(new object?[]{d.Definition,d.Result,d.Telemetry,d.Observations,d.Documentation,d.Sessions,d.Configuration}.Count(v=>v is not null)!=1)
                throw new ArgumentException("Each selected item must have one explicitly supported evidence type.");
            var valid=r.Kind switch{
                SubmissionEvidenceKind.Definition=>d.Definition is {} c&&c.Id==r.CardId&&c.Revision==binding.CardRevision&&c.OrganizationAssignment is null&&Hash(c)==binding.DefinitionSha256,
                SubmissionEvidenceKind.Result=>d.Result is {} result&&m.ResultIds.Contains(result.Id)&&result.Id==r.ResultId&&result.FlightId==m.FlightId&&result.Card is not null&&result.Card.Id==r.CardId&&result.Card.OrganizationAssignment is null&&Hash(result.Card)==binding.DefinitionSha256&&result.StartUtc==r.StartUtc&&result.EndUtc==r.EndUtc,
                SubmissionEvidenceKind.Telemetry=>d.Telemetry is {} t&&t.Samples is {Length:<=MaximumSamples}&&t.Breaks is {Length:<=MaximumSamples}&&t.Attempts is {Length:<=1000}&&Enum.IsDefined(t.FlightStatus)
                    &&t.Samples.All(s=>s is not null&&s.Frame is not null&&s.Sequence>0&&s.Frame.TimestampUtc>=r.StartUtc&&s.Frame.TimestampUtc<=r.EndUtc)&&t.Breaks.All(b=>b>=r.StartUtc&&b<=r.EndUtc)
                    &&t.Attempts.All(a=>a is not null&&a.FlightId==m.FlightId&&a.CardId==r.CardId&&a.CardRevision==binding.CardRevision&&a.StartUtc>=r.StartUtc&&a.EndUtc<=r.EndUtc),
                SubmissionEvidenceKind.ScientificObservations=>d.Observations is {Length:>=1 and <=MaximumSamples} observations&&observations.All(o=>o is not null&&o.MissionId==m.FlightId&&o.TimestampUtc>=r.StartUtc&&o.TimestampUtc<=r.EndUtc&&double.IsFinite(o.Value)&&o.Unit is {Length:<=80}&&o.Source is {Length:<=4000}),
                SubmissionEvidenceKind.PilotDocumentation=>d.Documentation is {Length:>=1 and <=MaximumSamples} docs&&docs.All(p=>p is not null&&p.TimestampUtc>=r.StartUtc&&p.TimestampUtc<=r.EndUtc&&p.Text is {Length:<=8000}&&p.Kind is not null&&p.Kind.StartsWith("Pilot Observation",StringComparison.Ordinal)&&p.SupportingObservationIds is {Length:0}),
                SubmissionEvidenceKind.CollectionSessions=>d.Sessions is {Length:>=1 and <=100} sessions&&sessions.All(s=>s is not null&&s.MissionId==m.FlightId),
                SubmissionEvidenceKind.MissionConfiguration=>d.Configuration is {} configuration&&configuration.FlightId==m.FlightId&&Digest(configuration.OriginalPlanSha256)&&configuration.Instruments is {Length:<=32}&&configuration.Instruments.All(i=>i is not null),
                _=>false};
            var records=d.Definition is not null||d.Result is not null?1:d.Telemetry?.Samples.LongLength??d.Observations?.LongLength??d.Documentation?.LongLength??d.Sessions?.LongLength??d.Configuration?.Instruments.LongLength??-1;
            if(records!=r.Records||d.Telemetry is {} originalTelemetry&&Hash(originalTelemetry)!=r.SourceSha256||d.Observations is {} originals&&(originals.Length!=1||Hash(originals[0])!=r.SourceSha256))valid=false;
            if(!valid)throw new ArgumentException("Selected evidence contains unsupported data or another flight/card's private context.");
        }
        foreach(var card in m.Cards)if(document.Evidence.Count(i=>i.Reference.CardId==card.CardId&&i.Data.Definition is not null)!=1)throw new ArgumentException("Each claim needs its exact scoped frozen card definition.");
        if(Bytes(document)>MaximumPayloadBytes)throw new ArgumentException("This evidence package exceeds 2 MiB. Select a smaller evidence interval or fewer optional items; originals are unchanged.");
    }
    public static SubmissionAssessment Assess(SubmissionDocument document,CancellationToken ct=default)
    {
        Validate(document,ct);var warnings=new List<string>();bool complete=true,supported=true;
        foreach(var binding in document.Manifest.Cards){
            ct.ThrowIfCancellationRequested();var items=document.Evidence.Where(i=>i.Reference.CardId==binding.CardId).ToArray();var card=items.Single(i=>i.Data.Definition is not null).Data.Definition!;
            var results=items.Where(i=>i.Data.Result is not null).Select(i=>i.Data.Result!).ToArray();var met=false;
            if(card.Procedure is not null||card.Survey is not null||card.ExperimentalSensor is not null){supported=false;warnings.Add(card.Name+": aggregate multi-point, raster and modeled-product completeness is not automatically verified by P17. Allow Incomplete Submission and manual review are required; unsupported binary products are not included.");}
            else if(card.FlightRegime is {} rule){
                foreach(var result in results){
                    var raw=items.SingleOrDefault(i=>i.Reference.ResultId==result.Id&&i.Data.Telemetry is not null)?.Data.Telemetry;
                    if(raw is null){warnings.Add(card.Name+": original supporting telemetry was not selected.");continue;}
                    if(!MissionStore.VerifySubmissionTelemetry(result,raw,ct))throw new ArgumentException("The selected original telemetry, interruption records or local result do not agree.");
                    var w=new FlightRegimeEvaluation.Window(rule,result.StartUtc,result.EndUtc);var breaks=raw.Breaks.Order().ToArray();var n=0;
                    foreach(var sample in raw.Samples){ct.ThrowIfCancellationRequested();while(n<breaks.Length&&breaks[n]<=sample.Frame.TimestampUtc){w.Break();n++;}w.Add(sample.Frame);}while(n++<breaks.Length)w.Break();
                    var interrupted=raw.FlightStatus!=MissionStatus.Completed||raw.Attempts.Any(a=>a.Status!=ResearchAttemptStatus.Recorded);
                    var outcome=w.Finish(interrupted);
                    if(outcome.Outcome!=result.Outcome||w.LongestHold!=result.LongestSupportedHoldSeconds)throw new ArgumentException("The uploaded local result does not match its selected original numeric evidence.");
                    if(outcome.Outcome!=ResearchAttemptOutcome.InsufficientEvidence)met=true;
                }
            }else if(AtmosphericResearch.IsCollectionCard(card)){
                var observations=items.SelectMany(i=>i.Data.Observations??[]).OrderBy(o=>o.TimestampUtc).ThenBy(o=>o.Id).ToArray();
                if(observations.Select(o=>o.Id).Distinct().Count()!=observations.Length)throw new ArgumentException("A scientific observation cannot be counted twice for the same card.");
                var sessions=items.SelectMany(i=>i.Data.Sessions??[]).Select(s=>s.Id).ToHashSet();
                if(observations.Any(o=>o.SessionId is null||!sessions.Contains(o.SessionId.Value)))warnings.Add(card.Name+": some selected observations have no included original session metadata. This traceability limitation requires reviewer attention; collection coverage is not scientific approval.");
                var continuity=items.Where(i=>i.Data.Telemetry is not null).Select(i=>i.Data.Telemetry!).ToArray();
                var breaks=continuity.SelectMany(i=>i.Breaks).Distinct().Order().ToArray();
                var request=new ResearchAttemptRequest(Guid.Empty,document.Manifest.FlightId,binding.StartUtc,binding.EndUtc);
                var c=AtmosphericResearch.Calculate(observations,card,Guid.Empty,breaks,ct,request);
                met=results.Length>0&&continuity.Length>0&&AtmosphericResearch.Outcome(c.Summary,card,continuity.Any(t=>t.FlightStatus!=MissionStatus.Completed||t.Attempts.Any(a=>a.Status!=ResearchAttemptStatus.Recorded))).Outcome==ResearchAttemptOutcome.CollectionComplete;
            }else if(card.ObservationFlight is {} observationRule){
                var docs=items.SelectMany(i=>i.Data.Documentation??[]).ToArray();if(docs.Select(d=>d.Id).Distinct().Count()!=docs.Length)throw new ArgumentException("A pilot observation cannot count twice for the same card.");
                var groups=observationRule.SelectedCategories.Select(category=>{var selected=docs.Where(d=>d.Category==category&&!string.IsNullOrWhiteSpace(d.Text)).ToArray();return new ObservationCategorySummary(category,selected.Length,selected.Count(d=>!observationRule.RequirePosition||ObservationResearch.SupportedPosition(d)));}).ToArray();
                var summary=new ObservationResearchSummary(1,groups.Sum(g=>g.Documented),groups.Sum(g=>g.Qualified),docs.Count(ObservationResearch.SupportedPosition),0,groups,"",null,"",[],[]);
                var continuity=items.Where(i=>i.Data.Telemetry is not null).Select(i=>i.Data.Telemetry!).ToArray();
                met=results.Length>0&&continuity.Length>0&&ObservationResearch.Outcome(summary,observationRule,continuity.Any(t=>t.FlightStatus!=MissionStatus.Completed||t.Attempts.Any(a=>a.Status!=ResearchAttemptStatus.Recorded))).Outcome==ResearchAttemptOutcome.CollectionComplete;
            }else{supported=false;warnings.Add(card.Name+": no supported automatic submission completeness rule; manual review is required.");}
            if(!met){complete=false;warnings.Add(card.Name+": selected evidence does not establish the supported minimum. Save a draft, add the original evidence, or use an explicitly published Allow Incomplete policy.");}
        }
        warnings.Add(HistoryDisclosure);warnings.Add("Server Received is not a reviewer decision, scientific validation or proof of real-world hardware accuracy. Simulator provenance and client-declared recording times remain subject to review.");
        return new(complete,supported,warnings.ToArray());
    }
    public static bool RequiredPoliciesMet(SubmissionDocument document,CancellationToken ct=default)
    {
        foreach(var binding in document.Manifest.Cards){
            var policy=document.Manifest.Policies.Single(p=>p.OpportunityId==binding.OpportunityId);if(policy.Mode==SubmissionPolicyMode.AllowIncompleteSubmission)continue;
            var scoped=document with{Manifest=document.Manifest with{Cards=[binding],Policies=[policy],Items=document.Manifest.Items.Where(i=>i.CardId==binding.CardId).ToArray()},Evidence=document.Evidence.Where(i=>i.Reference.CardId==binding.CardId).ToArray()};
            var a=Assess(scoped,ct);if(!a.MinimumMet||!a.RuleSupported)return false;
        }return true;
    }
}

public sealed partial class MissionStore
{
    public static bool VerifySubmissionTelemetry(PersonalResearchAttempt result,SubmissionTelemetry raw,CancellationToken ct=default)
    {
        if(result.Card.Procedure is not null)return false; // Aggregate procedure interpretation is explicitly manual in P17.
        using var hash=IncrementalHash.CreateHash(HashAlgorithmName.SHA256);var pauses=raw.Breaks.Order().ToArray();var i=0;long first=0,last=0,count=0;
        foreach(var s in raw.Samples){ct.ThrowIfCancellationRequested();while(i<pauses.Length&&pauses[i]<=s.Frame.TimestampUtc)AppendResearchBreak(hash,pauses[i++]);AppendResearchSample(hash,s.Sequence,JsonSerializer.Serialize(s.Frame,WireJson.Options));if(count++==0)first=s.Sequence;last=s.Sequence;}
        while(i<pauses.Length)AppendResearchBreak(hash,pauses[i++]);
        return count==result.Evidence.SampleCount&&first==result.Evidence.FirstSequence&&last==result.Evidence.LastSequence&&Convert.ToHexStringLower(hash.GetHashAndReset())==result.Evidence.Sha256;
    }
}
