namespace AeroScience.Shared;

// Server-owned definition metadata only. No desktop membership, assignment or evidence transfer implied.
public enum OrganizationFocus { Unknown, ScientificResearch, ExperimentalFlightTest, Mixed }
public enum OrganizationPublication { Draft, Published }
public static class OrganizationRoles
{
    public const string Administrator="Administrator",Manager="Manager",Reviewer="Reviewer",Pilot="Pilot";
    public static readonly string[] All=[Administrator,Manager,Reviewer,Pilot];
}
public sealed record OrganizationTemplateReference(string Id,int Version);
public sealed record OrganizationResearchDefinition(int Version,OrganizationTemplateReference Template,
    FlightRegimeDefinition? FlightRegime=null,AtmosphericResearchDefinition? AtmosphericProfile=null,
    AtmosphericTransectDefinition? AtmosphericTransect=null,SurveyResearchDefinition? Survey=null,
    ObservationResearchDefinition? ObservationFlight=null,ExperimentalSensorDefinition? ExperimentalSensor=null,
    FlightTestProcedureDefinition? Procedure=null);
public sealed record OrganizationTemplate(OrganizationTemplateReference Reference,string Name,string Description,OrganizationResearchDefinition Definition);
public sealed record OrganizationProgramRequest(int Version,Guid RequestId,int ExpectedRevision,string Name,string Description);
public sealed record OrganizationCampaignRequest(int Version,Guid RequestId,int ExpectedRevision,string Name,string Description,
    string Objective,OrganizationFocus Focus,OrganizationTemplateReference Template);
public sealed record OrganizationPublishRequest(int Version,Guid RequestId,int ExpectedRevision);
public sealed record OrganizationProgramView(Guid Id,int Revision,string Name,string Description,DateTimeOffset CreatedUtc,
    DateTimeOffset UpdatedUtc,Guid AuthorId,string AuthorName);
public sealed record OrganizationCampaignView(Guid Id,Guid ProgramId,int Revision,string Name,string Description,string Objective,
    OrganizationFocus Focus,OrganizationPublication Status,int? PublishedRevision,OrganizationResearchDefinition Definition,
    DateTimeOffset CreatedUtc,DateTimeOffset UpdatedUtc,Guid AuthorId,string AuthorName);
public sealed record OrganizationPage<T>(int Version,T[] Items,int? NextOffset);
