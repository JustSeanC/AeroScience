namespace AeroScience.Shared;

// A data-only envelope over the existing research definition, never an evidence archive.
public sealed record PortableResearchTemplate(string Format,int Version,string Name,string Description,
    OrganizationResearchDefinition Definition,int CardSchemaVersion,string[] RequiredCapabilities,
    string[] Limitations,string Attribution,string Sha256);
public sealed record PersonalTemplateProvenance(int Version,string Kind,string TemplateSha256,string Attribution,
    string Authorship="Self-declared attribution; not authenticated authorship or scientific approval",string Description="",int CardSchemaVersion=1);
public sealed record TemplateFileRequest(string Contents);
public sealed record TemplateImportRequest(string Contents,string Name);
public sealed record TemplateExportRequest(int Revision,string? Description=null,string? Attribution=null);
public sealed record TemplateCloneRequest(int Revision,string Name);
