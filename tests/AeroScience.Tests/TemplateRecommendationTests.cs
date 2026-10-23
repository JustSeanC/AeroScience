using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using AeroScience.Missions;
using AeroScience.Shared;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace AeroScience.Tests;

// Template/persistence fixtures, not sensor mounting or genuine-source acceptance.
public sealed class TemplateRecommendationTests
{
    [Fact]
    public void GeographicSurveyHasSurveyPrimariesAndExplicitlyDisabledOptionalContext()
    {
        var template = ScienceCatalog.Templates.Single(t => t.Id == MissionTemplateKind.GeographicSurvey);
        Assert.Equal(new[] { "terrain-scanner", "multispectral-imager" }, template.Instruments.Select(c => c.InstrumentId));
        Assert.All(template.Instruments, c => { Assert.True(c.Enabled); Assert.Equal(2000, c.IntervalMs); });
        Assert.NotNull(template.OptionalInstruments);
        Assert.Equal(new[] { "thermal-imager", "hyperspectral-imager", "ambient-temperature", "ambient-wind", "ambient-pressure", "ambient-density" },
            template.OptionalInstruments.Select(c => c.InstrumentId));
        Assert.All(template.OptionalInstruments, c => Assert.False(c.Enabled));
        var defaults = InstrumentProfiles.Freeze(ScienceCatalog.Normalize(template.Instruments));
        Assert.Equal(new[] { "terrain-scanner", "multispectral-imager" }.Order(), defaults.Where(c => c.Enabled).Select(c => c.InstrumentId).Order());
        Assert.All(defaults.Where(c => c.Enabled), c => Assert.NotNull(c.Payload));
        Assert.All(template.OptionalInstruments, optional => Assert.False(defaults.Single(c => c.InstrumentId == optional.InstrumentId).Enabled));
    }

    [Theory]
    [InlineData(MissionTemplateKind.GeneralResearch, "")]
    [InlineData(MissionTemplateKind.AtmosphericProfile, "ambient-temperature,ambient-pressure,ambient-wind,ambient-density")]
    [InlineData(MissionTemplateKind.ObservationFlight, "ambient-temperature")]
    [InlineData(MissionTemplateKind.CloudStructureSurvey, "ambient-temperature,ambient-wind,ambient-pressure")]
    [InlineData(MissionTemplateKind.AtmosphericTransect, "ambient-temperature,ambient-wind,ambient-pressure,ambient-density")]
    public void OtherMissionObjectivesRetainTheirDeliberatePayload(MissionTemplateKind kind, string expected)
    {
        var template = ScienceCatalog.Templates.Single(t => t.Id == kind);
        Assert.Equal(expected.Split(',', StringSplitOptions.RemoveEmptyEntries), template.Instruments.Select(c => c.InstrumentId));
        Assert.All(template.Instruments, c => Assert.True(c.Enabled));
        Assert.Null(template.OptionalInstruments);
        Assert.All(template.Instruments, c => Assert.Equal(kind == MissionTemplateKind.ObservationFlight ? 5000 : 1000, c.IntervalMs));
    }

    [Fact]
    public void AllRecommendationConfigurationsRemainValidUniqueAndAircraftAgnostic()
    {
        Assert.Equal(Enum.GetValues<MissionTemplateKind>().Length, ScienceCatalog.Templates.Length);
        foreach (var template in ScienceCatalog.Templates)
        {
            var candidates = template.Instruments.Concat(template.OptionalInstruments ?? []).ToArray();
            Assert.Equal(candidates.Length, candidates.Select(c => c.InstrumentId).Distinct().Count());
            ScienceCatalog.Normalize(candidates);
            foreach (var mode in Enum.GetValues<ScienceMode>())
            {
                var validated = MissionPlanning.Validate(new(Guid.NewGuid(), "Recommendation review", template.Id, mode));
                Assert.Equal(template.Instruments.Where(c => c.Enabled).Select(c => c.InstrumentId).Order(),
                    validated.Instruments!.Where(c => c.Enabled).Select(c => c.InstrumentId).Order());
                Assert.Null(validated.IntendedAircraft);
            }
        }
    }

    [Theory]
    [InlineData(ScienceMode.Universal)]
    [InlineData(ScienceMode.Realistic)]
    public void LegacyExplicitGeographicPayloadIsNotReplacedByNewDefaults(ScienceMode mode)
    {
        var json = """
            {"id":"0612efa4-f201-4ed8-b68f-2a4e795de012","name":"Legacy geography","template":"geographicSurvey",
             "instruments":[{"instrumentId":"ambient-temperature","enabled":true,"intervalMs":7000},
                            {"instrumentId":"ambient-wind","enabled":true,"intervalMs":11000}]}
            """;
        var plan = JsonSerializer.Deserialize<MissionPlan>(json, WireJson.Options)! with { Mode = mode };
        var validated = MissionPlanning.Validate(plan);
        Assert.Equal(new[] { "ambient-temperature", "ambient-wind" }, validated.Instruments!.Where(c => c.Enabled).Select(c => c.InstrumentId));
        Assert.Equal(7000, validated.Instruments!.Single(c => c.InstrumentId == "ambient-temperature").IntervalMs);
        Assert.Equal(11000, validated.Instruments!.Single(c => c.InstrumentId == "ambient-wind").IntervalMs);
        Assert.False(validated.Instruments!.Single(c => c.InstrumentId == "terrain-scanner").Enabled);
        Assert.False(validated.Instruments!.Single(c => c.InstrumentId == "multispectral-imager").Enabled);
        Assert.DoesNotContain(MissionPlanning.Validate(plan with { Instruments = [] }).Instruments!, c => c.Enabled);
    }

    [Fact]
    public void ExistingTemplateJsonStillRoundTripsWithoutInventedOptionalPayload()
    {
        const string json = """{"id":"observationFlight","name":"Observation Flight","objective":"Notes","instruments":[],"noteCategories":["observation"]}""";
        var legacy = JsonSerializer.Deserialize<MissionTemplate>(json, WireJson.Options)!;
        Assert.Null(legacy.OptionalInstruments);
        Assert.Equal(json, JsonSerializer.Serialize(legacy, WireJson.Options));
        using var response = JsonDocument.Parse(JsonSerializer.Serialize(ScienceCatalog.Templates, WireJson.Options));
        var geo = response.RootElement.EnumerateArray().Single(t => t.GetProperty("id").GetString() == "geographicSurvey");
        Assert.Equal(new[] { "terrain-scanner", "multispectral-imager" }, geo.GetProperty("instruments").EnumerateArray().Select(c => c.GetProperty("instrumentId").GetString()));
        Assert.All(geo.GetProperty("optionalInstruments").EnumerateArray(), c => Assert.False(c.GetProperty("enabled").GetBoolean()));
    }

    [Theory]
    [InlineData(ScienceMode.Universal)]
    [InlineData(ScienceMode.Realistic)]
    public void SavedCustomPayloadKeepsCalibrationCadenceProfilesAndGeometryAfterTemplateChange(ScienceMode mode)
    {
        var directory = Path.Combine(Path.GetTempPath(), "aeroscience-template-recommendations-" + Guid.NewGuid());
        try
        {
            var path = Path.Combine(directory, "missions.db");
            var store = new MissionStore(path); store.Initialize();
            var thermalProfile = InstrumentProfiles.Catalog.Single(p => p.Id == "flir.a655sc.lens-45");
            var payload = new PayloadConfiguration(thermalProfile with { Id = "user.synthetic.thermal", HorizontalFovDegrees = 31, Name = "Synthetic custom geometry" },
                ForwardDegrees: 3, RightDegrees: -2, Custom: true);
            var original = store.SavePlan(new(Guid.NewGuid(), "Custom archived plan", MissionTemplateKind.GeneralResearch, mode,
                Instruments: [new("ambient-temperature", true, 7000, Calibration: CalibrationTests.Definition()), new("thermal-imager", true, 11000, payload)],
                Waypoints: [new("Custom A", 40, -75), new("Custom B", 40.01, -74.99)],
                Patterns: [new(Guid.NewGuid(), FlightPatternKind.Lawnmower, new(40, -75), LengthMetres: 1234.56, WidthMetres: 789.12, SpacingMetres: 46.3)]));
            var changed = store.SavePlan(original with { Template = MissionTemplateKind.GeographicSurvey });
            var reopened = new MissionStore(path); reopened.Initialize();
            var actual = reopened.Plan(changed.Id)!;
            Assert.Equal(JsonSerializer.Serialize(original.Instruments, WireJson.Options), JsonSerializer.Serialize(actual.Instruments, WireJson.Options));
            Assert.Equal(JsonSerializer.Serialize(original.Patterns, WireJson.Options), JsonSerializer.Serialize(actual.Patterns, WireJson.Options));
            Assert.Equal(JsonSerializer.Serialize(original.Waypoints, WireJson.Options), JsonSerializer.Serialize(actual.Waypoints, WireJson.Options));
            Assert.Equal(MissionTemplateKind.GeographicSurvey, actual.Template);
            Assert.NotNull(actual.Instruments!.Single(c => c.InstrumentId == "ambient-temperature").Calibration);
            Assert.Equal(31, actual.Instruments!.Single(c => c.InstrumentId == "thermal-imager").Payload!.Profile.HorizontalFovDegrees);
            Assert.DoesNotContain(actual.Instruments!, c => c.Enabled && c.InstrumentId is "terrain-scanner" or "multispectral-imager");
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }

    [Fact]
    public async Task ActualTemplateApiReturnsExplicitSurveyRecommendationsWithoutCreatingMissions()
    {
        var directory = Path.Combine(Path.GetTempPath(), "aeroscience-template-api-" + Guid.NewGuid());
        var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Testing");
            builder.ConfigureAppConfiguration((_, configuration) => configuration.AddInMemoryCollection(new Dictionary<string, string?>
                { ["AeroScience:DataDirectory"] = directory }));
        });
        var client = factory.CreateClient(new() { BaseAddress = new("http://localhost:5080") });
        try
        {
            Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/templates")).StatusCode);
            client.DefaultRequestHeaders.Add("Origin", "http://localhost:5080");
            (await client.PostAsJsonAsync("/api/session", new { code = (string?)null })).EnsureSuccessStatusCode();
            var templates = (await client.GetFromJsonAsync<MissionTemplate[]>("/api/templates", WireJson.Options))!;
            var geographic = templates.Single(t => t.Id == MissionTemplateKind.GeographicSurvey);
            Assert.Equal(new[] { "terrain-scanner", "multispectral-imager" }, geographic.Instruments.Select(c => c.InstrumentId));
            Assert.Contains(geographic.OptionalInstruments!, c => c.InstrumentId == "thermal-imager" && !c.Enabled);
            Assert.Contains(geographic.OptionalInstruments!, c => c.InstrumentId == "hyperspectral-imager" && !c.Enabled);
            using var missions = JsonDocument.Parse(await client.GetStringAsync("/api/missions"));
            Assert.Equal(JsonValueKind.Array, missions.RootElement.ValueKind);
            Assert.Equal(0, missions.RootElement.GetArrayLength());
        }
        finally
        {
            await ApiHostLifetime.Stop(factory, client);
            if (Directory.Exists(directory)) Directory.Delete(directory, true);
        }
    }
}
