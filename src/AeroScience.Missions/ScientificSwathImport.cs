using AeroScience.Shared;
namespace AeroScience.Missions;
// The mission layer owns durable transfer jobs; the isolated decoder lives in Engine.
public interface IScientificSwathImport
{
    Task<ScientificSource?> FindReusable(NasaDownloadBundle bundle,CancellationToken token);
    Task<ScientificSource> ImportNasa(string folder,NasaDownloadBundle bundle,long networkBytes,CancellationToken token);
}
