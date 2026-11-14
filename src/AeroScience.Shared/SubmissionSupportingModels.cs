namespace AeroScience.Shared;

// v2 documents may explicitly attach v1 reference-only supporting products.
// No original binary/numeric payload, arbitrary URL, file path or automatic credit.
public sealed record SubmissionOriginalFileReference(int Ordinal,long Bytes,string Sha256);
public sealed record SubmissionSupportingSource(Guid Id,string Name,string Project,string Surface,string Unit,
    string HorizontalCrs,string VerticalReference,bool ReferenceVerified,DateTimeOffset? AcquisitionUtc,
    string ManifestSha256,SubmissionOriginalFileReference[] Originals,string[] Dimensions,string License,string Citation);
public sealed record SubmissionSupportingTile(Guid Id,string Sha256,long Bytes,Guid SourceId,long Values,long Missing);
public sealed record SubmissionSupportingProduct(int Version,Guid Id,Guid FlightId,string Kind,string Sha256,long Bytes,
    DateTimeOffset ProcessedUtc,Guid GeometryId,string GeometrySha256,string InputFingerprint,string Algorithm,string EvidenceClass,
    SubmissionSupportingSource[] Sources,SubmissionSupportingTile[] Tiles,long ReferencedValues,long ReferencedMissing,
    string[] Limitations,string Disclosure,bool ReferenceOnly,bool GrantsOriginalScienceCredit);
public sealed record SubmissionSupportingChoice(Guid Id,string Kind,string Sha256,long Bytes,string State,string? Reason);
public sealed record SubmissionSupportingChoices(bool DefinitionEligible,SubmissionSupportingChoice[] Items,string Disclosure);
public sealed record SubmissionSupportingRequest(Guid FlightId,Guid[] ResultIds);
