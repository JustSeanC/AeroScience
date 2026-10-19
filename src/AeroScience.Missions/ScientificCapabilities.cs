using AeroScience.Shared;
namespace AeroScience.Missions;

public static class ScientificCapabilities
{
    public static bool Supports(PayloadConfiguration payload,ScientificSource source)=>
        Describe(source).Capabilities.Any(payload.Profile.CompatibleCapabilities.Contains);
    public static SourceCapabilitySummary Describe(ScientificSource source)
    {
        var m=source.Manifest;
        ScientificCapability[] capabilities=m.Kind switch
        {
            RemoteKind.Thermal=>[ScientificCapability.SurfaceTemperature],
            RemoteKind.Multispectral=>[ScientificCapability.MultispectralReflectance],
            RemoteKind.Hyperspectral=>m.Bands.Where(b=>!b.BadBand).All(b=>b.Unit=="reflectance")?[ScientificCapability.HyperspectralReflectance]:[ScientificCapability.HyperspectralRadiance],
            RemoteKind.Terrain=>m.Acquisition?.Workflow=="copernicus-dem"||m.Provider.Contains("Copernicus",StringComparison.OrdinalIgnoreCase)?[ScientificCapability.Elevation,ScientificCapability.SurfaceModel]:[ScientificCapability.Elevation],
            _=>[]
        };
        return new(source.Id,capabilities,m.Provider,m.Acquisition?.Catalog??m.SceneId,
            ["Capabilities describe this retained numeric source, independently of a modeled hardware profile. Compatibility does not imply matching spectral response, calibration or resolution.",
             m.Kind==RemoteKind.Terrain?"Elevation rasters are not point clouds. Bare-earth/DSM identity and vertical datum must be checked in source metadata.":"Original source bands, masks, native resolution and observation date remain authoritative."]);
    }
}
