using System.Globalization;
using System.Text.Json;
using AeroScience.Shared;

namespace AeroScience.Missions;

public sealed record HyperspectralUserMetadata(string? Name=null,string? Provider=null,string? Source=null,
    DateTimeOffset? ObservationUtc=null,string? License=null,string? Notes=null,string? Unit=null,string? VerticalDatum=null,string? SceneId=null,double? Scale=null,double? Offset=null);
public sealed record HyperspectralInspection(string Header,string? Binary,string[] Candidates,int BandCount,double? MinimumWavelengthNm,
    double? MaximumWavelengthNm,string? Unit,RasterGrid? Grid,Coordinates[] Coverage,long FileBytes,long EstimatedImportBytes,
    DateTimeOffset? ObservationUtc,int BadBands,string[] Warnings,ScientificSourceManifest? Manifest,string Root,RemoteKind Kind=RemoteKind.Hyperspectral);
public sealed record HyperspectralImportState(string Stage,string Message,HyperspectralInspection? Inspection=null,
    ScientificSource? Source=null,string? Destination=null,long? FreeBytes=null,bool Busy=false,ScientificCatalogItem? NasaSelection=null);

public static class GuidedHyperspectralImport
{
    private static readonly CultureInfo Inv=CultureInfo.InvariantCulture;
    private static double[] Numbers(string value)=>value.Split(',').Select(v=>double.Parse(v.Trim(),NumberStyles.Float,Inv)).ToArray();
    public static string FindHeader(string selected)
    {
        StorageManager.ValidateLocalPath(selected);
        if(File.Exists(selected))
        {if(!Path.GetExtension(selected).Equals(".hdr",StringComparison.OrdinalIgnoreCase))throw new InvalidDataException("Choose an ENVI .hdr header. NetCDF, HDF, JP2 and RGB previews cannot be imported as hyperspectral cubes.");return Path.GetFullPath(selected);}
        if(!Directory.Exists(selected))throw new IOException("The dataset folder or header could not be found.");
        var headers=Directory.EnumerateFiles(selected).Where(f=>Path.GetExtension(f).Equals(".hdr",StringComparison.OrdinalIgnoreCase)).Take(65).ToArray();
        return headers.Length==1?headers[0]:throw new InvalidDataException(headers.Length==0?"No ENVI .hdr file is in this folder. Extract the dataset first, then choose its header.":"This folder has multiple headers. Choose the specific dataset's .hdr file so its identity is unambiguous.");
    }
    public static HyperspectralInspection Inspect(string selected,HyperspectralUserMetadata? metadata=null,string? associated=null)
    {
        metadata??=new();var header=FindHeader(selected);var root=Path.GetDirectoryName(header)!;var headerName=Path.GetFileName(header);
        _=ScientificRaster.SafeFile(root,headerName);var fields=ScientificRaster.ReadEnviFields(header);
        string Field(string key)=>fields.TryGetValue(key,out var value)?value:throw new InvalidDataException("Missing ENVI "+key+". Obtain a compatible original with this metadata; AeroScience will not guess it.");
        var count=int.Parse(Field("bands"),Inv);if(count is <16 or >512)throw new InvalidDataException("Compatible hyperspectral cubes require 16–512 original spectral bands.");
        var wavelengths=Numbers(Field("wavelength"));var wavelengthUnit=Field("wavelength units").Trim().ToLowerInvariant();
        if(wavelengthUnit is not("nanometers" or "micrometers" or "nm" or "um"))throw new InvalidDataException("Wavelength units must state nanometers or micrometers.");
        if(wavelengthUnit is "micrometers" or "um")wavelengths=wavelengths.Select(w=>w*1000).ToArray();
        if(wavelengths.Length!=count||wavelengths.Any(w=>!double.IsFinite(w)||w is <=0 or >=100000)||wavelengths.Distinct().Count()!=count)throw new InvalidDataException("Wavelength metadata must contain one finite, distinct wavelength per original band.");
        var bad=fields.TryGetValue("bbl",out var bbl)?Numbers(bbl):Enumerable.Repeat(1d,count).ToArray();
        if(bad.Length!=count||bad.Any(b=>b is not(0 or 1))||bad.All(b=>b==0))throw new InvalidDataException("Bad-band metadata must contain one 0/1 flag per band and at least one usable band.");
        var samples=long.Parse(Field("samples"),Inv);var lines=long.Parse(Field("lines"),Inv);var type=int.Parse(Field("data type"),Inv);
        var size=type switch{1=>1,2 or 12=>2,3 or 4 or 13=>4,5=>8,_=>throw new InvalidDataException("Unsupported ENVI numeric data type.")};
        if(samples is <1 or >1000000||lines is <1 or >1000000)throw new InvalidDataException("Invalid ENVI image dimensions.");
        var expected=checked(samples*lines*count*size+(fields.TryGetValue("header offset",out var offset)?long.Parse(offset,Inv):0));
        var known=new List<string>();
        void Candidate(string file){if(!known.Contains(file,StringComparer.OrdinalIgnoreCase)&&File.Exists(Path.Combine(root,file)))known.Add(file);}
        if(fields.TryGetValue("data file",out var dataFile))Candidate(dataFile.Trim('"','\''));
        Candidate(Path.GetFileNameWithoutExtension(header));var stem=Path.GetFileNameWithoutExtension(header);foreach(var extension in new[]{".bin",".img",".dat",".raw",""})Candidate(stem+extension);
        if(known.Count==0)foreach(var file in Directory.EnumerateFiles(root).Take(1024))if(!Path.GetExtension(file).Equals(".hdr",StringComparison.OrdinalIgnoreCase)&&new FileInfo(file).Length==expected)Candidate(Path.GetFileName(file));
        var candidates=known.Where(file=>new FileInfo(ScientificRaster.SafeFile(root,file)).Length==expected).ToArray();
        string? binary=associated;
        if(binary is not null){if(!candidates.Contains(binary,StringComparer.OrdinalIgnoreCase))throw new InvalidDataException("Select an associated binary in the header folder with exactly the declared original image size.");}
        else if(candidates.Length==1)binary=candidates[0];
        if(binary is null)return new(header,null,candidates,count,wavelengths.Min(),wavelengths.Max(),null,null,[],new FileInfo(header).Length,expected+1048576,null,bad.Count(b=>b==0),[candidates.Length==0?"The companion image file could not be found. Keep the original binary and .hdr together.":"Several binaries match this header. Select the correct associated file before continuing."],null,root);
        var warnings=new List<string>();
        string? unit=null;
        if(fields.TryGetValue("data units",out var rawUnit))unit=rawUnit.Trim().ToLowerInvariant() switch{"reflectance"=>"reflectance","radiance w/(m2 sr um)"=>"radiance W/(m2 sr um)","radiance w/(m2 sr nm)"=>"radiance W/(m2 sr nm)","radiance mw/(cm2 sr um)"=>"radiance mW/(cm2 sr um)","radiance uw/(cm2 sr nm)"=>"radiance uW/(cm2 sr nm)",_=>null};
        if(unit is null&&fields.ContainsKey("reflectance scale factor"))unit="reflectance";
        if(unit is not null&&metadata.Unit is not null&&unit!=metadata.Unit)throw new InvalidDataException("The chosen scientific units conflict with the original ENVI header. Source units cannot be relabeled; use the documented original units.");
        unit??=metadata.Unit;
        if(unit is not null&&unit is not("reflectance" or "radiance W/(m2 sr um)" or "radiance W/(m2 sr nm)" or "radiance mW/(cm2 sr um)" or "radiance uW/(cm2 sr nm)"))throw new InvalidDataException("Select the original documented reflectance or radiance units; RGB/display brightness is not hyperspectral science.");
        if(unit is null)warnings.Add("Scientific units are not identifiable in this header. Select the documented original units; radiance is never converted to reflectance.");
        double? noData=fields.TryGetValue("data ignore value",out var ignore)?double.Parse(ignore,Inv):null;
        var scale=fields.TryGetValue("reflectance scale factor",out var factor)?1/double.Parse(factor,Inv):1;
        var gains=fields.TryGetValue("data gain values",out var gainValues)?Numbers(gainValues):null;var offsets=fields.TryGetValue("data offset values",out var offsetValues)?Numbers(offsetValues):null;
        if(gains is not null&&(gains.Length!=count||gains.Any(v=>!double.IsFinite(v)||v==0))||offsets is not null&&(offsets.Length!=count||offsets.Any(v=>!double.IsFinite(v)))||!double.IsFinite(scale)||scale==0)throw new InvalidDataException("Invalid ENVI band scaling metadata.");
        if(gains is not null&&fields.ContainsKey("reflectance scale factor"))throw new InvalidDataException("Both reflectance scale and band gain are supplied. This ambiguous calibration must be clarified with the data provider before import.");
        DateTimeOffset? observed=metadata.ObservationUtc;
        if(observed is null&&fields.TryGetValue("acquisition time",out var acquisition)&&(acquisition.EndsWith("Z",StringComparison.OrdinalIgnoreCase)||acquisition.Length>=6&&acquisition[^6] is '+' or '-'&&acquisition[^3]==':')&&DateTimeOffset.TryParse(acquisition,Inv,DateTimeStyles.None,out var date))observed=date.ToUniversalTime();
        if(observed is null)warnings.Add("Original acquisition date is unknown; it will remain unknown.");
        if(!fields.ContainsKey("bbl"))warnings.Add("No bad-band list supplied; no source quality mask has been invented.");
        if(wavelengths.Zip(wavelengths.Skip(1)).Any(p=>p.First>=p.Second))warnings.Add("Detector wavelength overlaps: display/manifest bands are sorted by wavelength while original binary channel numbers and bad-band flags are preserved. Original data are not rewritten.");
        var bands=Enumerable.Range(0,count).Select(i=>new SourceBand("spectral-"+i.ToString("D3",Inv),binary,"envi",i,unit??"1",gains?[i]??scale,offsets?[i]??0,noData,wavelengths[i],bad[i]==0,headerName,EnviMapReference:"envi-corner-v2")).OrderBy(b=>b.WavelengthNm).ToArray();
        using var raster=new ScientificRaster(root,bands[0]);var grid=raster.Grid;
        var coverage=ScientificRaster.GridCoverage(grid);
        if(coverage.Any(p=>!MissionPlanning.Position(p.Latitude,p.Longitude)))throw new InvalidDataException("The dataset has no usable geographic coordinates.");
        var derived=new{header=headerName,binary,bands=count,wavelengthUnit,grid,observationFromHeader=fields.GetValueOrDefault("acquisition time"),originalDataUnits=fields.GetValueOrDefault("data units"),badBandListSupplied=fields.ContainsKey("bbl"),metadataOrigin="ENVI header; user-entered additions below are not provider-verified",userEntered=metadata};
        var manifest=unit is null?null:ScienceStore.ValidateSourceManifest(new(1,metadata.Name??stem,RemoteKind.Hyperspectral,metadata.Provider??"User supplied; provider not specified",metadata.License??"Usage terms not supplied; user must verify rights",metadata.Source??"Original local ENVI dataset; source link not supplied",stem,observed,"not applicable",bands,Notes:JsonSerializer.Serialize(derived,WireJson.Options)));
        var bytes=new FileInfo(header).Length+new FileInfo(Path.Combine(root,binary)).Length;
        return new(header,binary,candidates,count,wavelengths.Min(),wavelengths.Max(),unit,grid,coverage,bytes,bytes+1048576,observed,bad.Count(b=>b==0),warnings.ToArray(),manifest,root);
    }
}

// One graphical import session. HTTP callers cannot supply filesystem paths.
public sealed class ScientificImportSession(ScienceStore science,Action? imported=null):IDisposable
{
    private readonly object gate=new();private CancellationTokenSource? cancel;private Task? active;private int generation;private RemoteKind kind=RemoteKind.Hyperspectral;private string? selected,associated;private HyperspectralUserMetadata metadata=new();
    private ScientificCatalogItem? nasa;
    private HyperspectralImportState state=new("Choose","Choose an ENVI header or dataset folder. Original files remain untouched.");
    public bool IsBusy {get{lock(gate)return state.Busy;}}
    public HyperspectralImportState Snapshot(){lock(gate)return state with{NasaSelection=nasa};}
    public void BeginNasa(ScientificCatalogItem item)
    {
        if(item.Nasa is null||!item.Nasa.Capability.StartsWith("Guided import",StringComparison.Ordinal)||item.Provider is not("nasa-classic" or "nasa-ng"))throw new ArgumentException("EMIT and unknown NASA products cannot be imported. Choose a compatible AVIRIS reflectance granule.");
        lock(gate){if(state.Busy)throw new InvalidOperationException("Finish or cancel the active import before selecting another NASA scene.");generation++;selected=associated=null;nasa=item;kind=RemoteKind.Hyperspectral;metadata=new();state=new("Choose","NASA browser acquisition is external. Download the original matching reflectance .hdr and numeric binary, then choose the header on the companion PC. No NASA password is requested here.");}
    }
    private void Set(HyperspectralImportState value){lock(gate)state=value;}
    public async Task Select(string path,RemoteKind type=RemoteKind.Hyperspectral)
    {
        lock(gate){if(state.Busy)throw new InvalidOperationException("Wait for the current inspection/import.");generation++;kind=type;selected=path;associated=null;metadata=new();state=new("Verifying","Reading bounded ENVI metadata…",Busy:true);}
        await Verify();
    }
    public async Task Associate(string file)
    {lock(gate){if(state.Busy||selected is null)throw new InvalidOperationException("Choose a header first.");associated=file;state=state with{Busy=true};}await Verify();}
    public async Task Configure(HyperspectralUserMetadata value)
    {
        if(new[]{value.Name,value.Provider,value.Source,value.License,value.Notes,value.SceneId,value.VerticalDatum}.Any(s=>s?.Length>2000))throw new ArgumentException("Keep each source-information field under 2000 characters.");
lock(gate){if(state.Busy||selected is null)throw new InvalidOperationException("Choose a header and wait for inspection first.");if(nasa is not null&&new[]{value.Provider,value.Source,value.License,value.Unit,value.SceneId,value.VerticalDatum}.Any(v=>v is not null)||nasa is not null&&(value.ObservationUtc is not null||value.Scale is not null||value.Offset is not null))throw new ArgumentException("NASA source identity, time and scientific units are verified from the selected product and original files; they cannot be overridden. Cancel for advanced manual import.");metadata=value;state=state with{Busy=true};}await Verify();}
    private async Task Verify()
    {
        var own=generation;CancellationToken token;lock(gate){cancel?.Dispose();cancel=new();token=cancel.Token;}
        try
        {
            var inspection=await Task.Run(()=>kind==RemoteKind.Hyperspectral?nasa is not null?NasaGuidedImport.Inspect(selected!,nasa,metadata,associated,token):GuidedHyperspectralImport.Inspect(selected!,metadata,associated):GuidedScientificImport.Inspect(selected!,kind,metadata));
            var free=new LocalStorageCapacity().FreeBytes(science.DirectoryPath);
            if(own!=generation)return;
            Set(new("Verify",inspection.Manifest is null?"Select the associated binary or original documented scientific units before importing.":"Compatible ENVI dataset. Review its actual geographic coverage and metadata.",inspection,Destination:science.DirectoryPath,FreeBytes:free));
        }
        catch(Exception e)when(e is not OutOfMemoryException){if(own==generation)Set(new("Invalid",e.Message));}
    }
    public void Import()
    {
        HyperspectralInspection inspection;CancellationToken token;
        lock(gate){if(state.Busy||state.Inspection is not{Manifest:not null} i)throw new InvalidOperationException("Verify a compatible dataset before importing.");inspection=i;cancel?.Dispose();cancel=new();token=cancel.Token;state=state with{Stage="Importing",Message="Validating and copying original files into the Scientific Data Library…",Busy=true};}
        active=Task.Run(async()=>
        {
            try
            {
                if(nasa is not null)inspection=NasaGuidedImport.Inspect(selected!,nasa,metadata,associated,token);
                var source=await science.ImportSourceAsync(inspection.Manifest!,inspection.Root,token,new Progress<string>(message=>{lock(gate)state=state with{Message=message};}),reuseExisting:true,verifyStaged:nasa is null?null:staged=>
                {
                    var verified=NasaGuidedImport.Inspect(Path.Combine(staged,Path.GetFileName(inspection.Header)),nasa,metadata,associated,token);
                    if(ScientificCatalog.Hash(verified.Manifest)!=ScientificCatalog.Hash(inspection.Manifest))throw new InvalidDataException("NASA metadata changed during import; no source was published.");
                });
                imported?.Invoke();
                Set(new("Ready","Dataset Ready. It can be sampled only inside its real coverage; original external files are retained.",inspection,source,science.DirectoryPath));
            }
            catch(OperationCanceledException){lock(gate)nasa=null;Set(new("Cancelled","Import cancelled. No partial source is advertised as ready. Original files are unchanged; interrupted staging is available in Storage Management."));}
            catch(Exception e)when(e is IOException or InvalidDataException or ArgumentException or InvalidOperationException or UnauthorizedAccessException or Microsoft.Data.Sqlite.SqliteException){Set(new("Invalid",e.Message));}
        });
    }
    public void Cancel(){lock(gate){cancel?.Cancel();generation++;if(state.Stage!="Importing"){nasa=null;state=new("Choose","Import cancelled. Original files are unchanged.");}}}
    public void Dispose(){Cancel();active?.GetAwaiter().GetResult();}
}
