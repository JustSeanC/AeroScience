using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using AeroScience.Shared;

namespace AeroScience.Missions;

public static class PortableResearchTemplates
{
    public const string Format="AeroScience.ResearchTemplate";
    public const int MaximumBytes=16384;
    static readonly JsonSerializerOptions Strict=new(JsonSerializerDefaults.Web){MaxDepth=16,
        PropertyNameCaseInsensitive=false,UnmappedMemberHandling=JsonUnmappedMemberHandling.Disallow,
        Converters={new JsonStringEnumConverter(JsonNamingPolicy.CamelCase,allowIntegerValues:false)}};
    public static string Hash(object value)=>Convert.ToHexStringLower(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(value,WireJson.Options)));
    public static string Text(string? value,int max,string label,bool required=false)
    {
        var text=value?.Trim()??"";
        if(text.Length>max||required&&text.Length==0||text.Any(c=>char.IsControl(c)&&c is not ('\n' or '\r' or '\t'))
            ||Regex.IsMatch(text,@"(?i)(<\s*/?\s*[a-z][^>]*>|javascript\s*:|file\s*:|[a-z]:[\\/]|\\\\|(^|\s)/[a-z0-9._-]+/|^#!|\beval\s*\()"))
            throw new ArgumentException($"{label} must be readable text, at most {max} characters, without executable content or filesystem paths.");
        return text;
    }
    static void SafeDefinition(object? value)
    {
        if(value is null)return;
        if(value is string text){Text(text,4000,"Definition text");return;}
        var type=value.GetType();if(type.IsPrimitive||type.IsEnum||value is decimal or Guid)return;
        if(value is System.Collections.IEnumerable list){foreach(var item in list)SafeDefinition(item);return;}
        foreach(var p in type.GetProperties(BindingFlags.Public|BindingFlags.Instance))
        {
            if(p.GetCustomAttribute<JsonIgnoreAttribute>()?.Condition==JsonIgnoreCondition.Always)continue;
            var item=p.GetValue(value);
            if(p.GetCustomAttribute<JsonExtensionDataAttribute>() is not null)
            {if(item is System.Collections.IDictionary {Count:>0})throw new ArgumentException("Unknown future criteria cannot be imported or simplified. Keep the original file for a compatible version.");continue;}
            SafeDefinition(item);
        }
    }
    public static int ValidateDefinition(OrganizationResearchDefinition? d)
    {
        if(d is null||d.Version!=1||d.Template is null||d.Template.Version!=1)throw new ArgumentException("Unsupported research schema or template version.");
        SafeDefinition(d);
        if(new object?[]{d.FlightRegime,d.AtmosphericProfile,d.AtmosphericTransect,d.Survey,d.ObservationFlight,d.ExperimentalSensor,d.Procedure}.Count(x=>x is not null)!=1)
            throw new ArgumentException("A template must contain exactly one supported research definition.");
        string kind;int version;
        if(d.Procedure is not null){FlightRegimeEvaluation.ValidateProcedure(d.Procedure);kind=FlightRegimeEvaluation.ProcedureTemplate;version=8;}
        else if(d.ExperimentalSensor is not null){ExperimentalSensorResearch.Validate(d.ExperimentalSensor);kind=ExperimentalSensorResearch.Template;version=6;}
        else if(d.ObservationFlight is not null){ObservationResearch.Validate(d.ObservationFlight);kind=ObservationResearch.Template;version=5;}
        else if(d.Survey is not null){SurveyResearch.Validate(d.Survey);kind=SurveyResearch.Template;version=4;}
        else if(d.AtmosphericTransect is not null){AtmosphericTransectResearch.Validate(d.AtmosphericTransect);kind=AtmosphericTransectResearch.Template;version=3;}
        else if(d.AtmosphericProfile is not null){AtmosphericResearch.Validate(d.AtmosphericProfile);kind=AtmosphericResearch.Template;version=2;}
        else{FlightRegimeEvaluation.Validate(d.FlightRegime);kind=d.FlightRegime!.TestPoint is null?"flight-regime":FlightRegimeEvaluation.CustomTemplate;version=kind=="flight-regime"?1:7;}
        if(d.Template.Id!=kind)throw new ArgumentException("The declared research type does not match its criteria.");
        return version;
    }
    static string[] Capabilities(OrganizationResearchDefinition d)
    {
        if(d.Procedure is not null||d.FlightRegime is not null)
        {var rules=d.Procedure?.Points.Select(p=>p.Conditions)??[d.FlightRegime!];return rules.SelectMany(r=>new[]{FlightRegimeEvaluation.RequiresAirspeed(r)?"simulator:indicated-airspeed-knots":null,FlightRegimeEvaluation.RequiresAltitude(r)?"simulator:altitude-feet-msl":null}).OfType<string>().Distinct().Order().ToArray();}
        if(d.Survey is not null)return d.Survey.Measurements.Select(m=>m switch{SurveyMeasurement.AirTemperature=>"simulator:ambient-temperature",SurveyMeasurement.TerrainElevation=>"historical:dem-elevation",_=>"historical:landsat-collection-2-surface-temperature"}).Order().ToArray();
        if(d.ObservationFlight is not null)return new[]{"pilot:documented-observation"}.Concat(d.ObservationFlight.SelectedInstruments.Select(i=>"optional-instrument:"+i)).Order().ToArray();
        return ["simulator:ambient-temperature",d.ExperimentalSensor is not null?"model:experimental-temperature-v1":"simulator:altitude-feet-msl"];
    }
    static string[] Limits(OrganizationResearchDefinition d)=>[
        "A shared definition is not collected evidence, aircraft certification, sensor calibration or organization approval.",
        "Importing grants no network, download, organization or upload permission. Missing observations, pauses and gaps remain missing evidence.",
        d.ExperimentalSensor is not null?"Fictional deterministic sensor response derived from retained simulator temperature inputs; not physical hardware validation.":
        d.Survey is not null?"DEM elevation and Landsat surface temperature require compatible historical sources, original dates and valid QA; they are not new airborne LiDAR or thermal images.":
        d.ObservationFlight is not null?"Pilot-written observations are descriptions, not automatically verified sensor measurements or proof of a hypothesis.":
        d.FlightRegime is not null||d.Procedure is not null?"IAS and MSL altitude use supported recorded simulator telemetry. Example targets are not safe operating limits for every aircraft.":
        "Atmospheric measurements describe supported simulator conditions at recorded samples, not Earth's actual atmosphere or unsampled altitudes."];
    public static PortableResearchTemplate Create(string name,string description,OrganizationResearchDefinition d,string attribution="")
    {
        var version=ValidateDefinition(d);
        var result=new PortableResearchTemplate(Format,1,Text(name,100,"Template name",true),Text(description,2000,"Description"),d,version,Capabilities(d),Limits(d),Text(attribution,500,"Attribution"),"");
        result=result with{Sha256=Hash(result)};
        if(Encoding.UTF8.GetByteCount(JsonSerializer.Serialize(result,WireJson.Options))>MaximumBytes)throw new ArgumentException("Template exceeds the 16 KiB portable-file limit.");
        return result;
    }
    static void UniqueKeys(JsonElement element)
    {
        if(element.ValueKind==JsonValueKind.Object){var names=new HashSet<string>(StringComparer.Ordinal);foreach(var p in element.EnumerateObject()){if(!names.Add(p.Name))throw new ArgumentException("Duplicate fields are not allowed in portable templates.");UniqueKeys(p.Value);}}
        else if(element.ValueKind==JsonValueKind.Array)foreach(var item in element.EnumerateArray())UniqueKeys(item);
    }
    static void CompleteShape(JsonElement original,JsonElement typed)
    {
        // Do not fill an absent criterion/version with a constructor's example default.
        if(original.ValueKind==JsonValueKind.Object&&typed.ValueKind==JsonValueKind.Object)
        {
            var names=original.EnumerateObject().Select(p=>p.Name).ToHashSet(StringComparer.Ordinal);
            if(!names.SetEquals(typed.EnumerateObject().Select(p=>p.Name)))throw new ArgumentException("Template fields are incomplete or unsupported. Export the complete definition from a compatible AeroScience version.");
            foreach(var p in typed.EnumerateObject())CompleteShape(original.GetProperty(p.Name),p.Value);
        }
        else if(original.ValueKind==JsonValueKind.Array&&typed.ValueKind==JsonValueKind.Array)
        {for(var i=0;i<original.GetArrayLength();i++)CompleteShape(original[i],typed[i]);}
    }
    public static PortableResearchTemplate Read(string? contents)=>Parse(contents,false);
    // Only an explicit manager edit uses this; file import/proposal always verifies integrity.
    public static PortableResearchTemplate Revise(string? contents)=>Parse(contents,true);
    static PortableResearchTemplate Parse(string? contents,bool editing)
    {
        if(contents is null||Encoding.UTF8.GetByteCount(contents)>MaximumBytes)throw new ArgumentException("Choose a JSON research template no larger than 16 KiB.");
        try
        {
            using var doc=JsonDocument.Parse(contents,new(){MaxDepth=16});UniqueKeys(doc.RootElement);
            var value=JsonSerializer.Deserialize<PortableResearchTemplate>(contents,Strict)??throw new ArgumentException("Empty template.");
            CompleteShape(doc.RootElement,JsonSerializer.SerializeToElement(value,WireJson.Options));
            if(value.Format!=Format||value.Version!=1)throw new ArgumentException("Unsupported portable template format or version. The original file has not changed.");
            var expected=Create(value.Name,value.Description,value.Definition,value.Attribution);
            if(editing)return expected;
            if(value.CardSchemaVersion!=expected.CardSchemaVersion||value.RequiredCapabilities is null||!value.RequiredCapabilities.SequenceEqual(expected.RequiredCapabilities)
                ||value.Limitations is null||!value.Limitations.SequenceEqual(expected.Limitations)||value.Sha256!=Hash(value with{Sha256=""}))
                throw new ArgumentException("Template integrity, schema, capabilities or scientific limitations do not match. Keep the original file; no criteria were imported.");
            return value;
        }
        catch(JsonException){throw new ArgumentException("Invalid, unknown or too deeply nested template fields. The original file has not changed.");}
    }
    public static OrganizationResearchDefinition Definition(PersonalResearchCard c)=>new(1,new(c.TemplateId,1),c.FlightRegime,c.AtmosphericProfile,c.AtmosphericTransect,c.Survey,c.ObservationFlight,c.ExperimentalSensor,c.Procedure);
    public static SaveResearchCardRequest SaveRequest(PortableResearchTemplate t,string name)=>new(name,t.Definition.FlightRegime,
        AtmosphericProfile:t.Definition.AtmosphericProfile,AtmosphericTransect:t.Definition.AtmosphericTransect,Survey:t.Definition.Survey,
        ObservationFlight:t.Definition.ObservationFlight,ExperimentalSensor:t.Definition.ExperimentalSensor,Procedure:t.Definition.Procedure);
}

public sealed partial class MissionStore
{
    public PortableResearchTemplate ExportResearchTemplate(Guid id,TemplateExportRequest request)
    {
        var card=ResearchCard(id,request.Revision);SupportedCard(card);
        return PortableResearchTemplates.Create(card.Name,request.Description,PortableResearchTemplates.Definition(card),
            request.Attribution.Length>0?request.Attribution:card.TemplateProvenance?.Attribution??"");
    }
    public PersonalResearchCard ImportResearchTemplate(TemplateImportRequest request)
    {
        var t=PortableResearchTemplates.Read(request.Contents);
        return SaveResearchCard(PortableResearchTemplates.SaveRequest(t,request.Name),new(1,"Imported",t.Sha256,t.Attribution));
    }
    public PersonalResearchCard CloneResearchTemplate(Guid id,TemplateCloneRequest request)
    {
        var t=ExportResearchTemplate(id,new(request.Revision));
        return SaveResearchCard(PortableResearchTemplates.SaveRequest(t,request.Name),new(1,"Cloned",t.Sha256,t.Attribution));
    }
}
