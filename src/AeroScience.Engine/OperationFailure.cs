using AeroScience.Missions;

namespace AeroScience.Engine;

public sealed record OperationFailure(int Status,string Code,string Category,string Error)
{
    public static OperationFailure Describe(Exception error)=>ScientificFailure.Kind(error) switch
    {
        ScientificFailureKind.ProviderRedirect=>new(502,"provider_redirect_rejected","provider",error.Message),
        ScientificFailureKind.Authorization=>new(409,"provider_authorization_required","authorization",error.Message),
        ScientificFailureKind.ProviderUnavailable=>new(502,"provider_unavailable","provider",error.Message),
        ScientificFailureKind.UnsupportedFormat=>new(422,"scientific_format_unsupported","format",error.Message),
        ScientificFailureKind.ScientificIntegrity=>new(409,"scientific_integrity_failed","scientific-validation",error.Message),
        _=>error switch{
            StorageLimitException=>new(409,"storage_capacity_insufficient","storage",error.Message),
            DirectoryNotFoundException or FileNotFoundException=>new(409,"selected_resource_unavailable","resource",error.Message),
            UnauthorizedAccessException=>new(409,"operation_permission_denied","permission",error.Message),
            Microsoft.Data.Sqlite.SqliteException=>new(409,"database_operation_failed","database",error.Message),
            HttpRequestException=>new(502,"provider_network_failed","network","The provider request could not complete. Check connectivity, retry, or use official browser access. Existing scientific data was preserved."),
            _=>new(409,"operation_io_failed","operation",error.Message)
        }
    };
}
