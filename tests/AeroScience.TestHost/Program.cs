using AeroScience.Engine;
using AeroScience.Telemetry;

// This executable is built only for automated tests and never published with the app.
var app = EngineApplication.Create(args, services => services.AddSingleton<ITelemetryProvider>(sp =>
    new SimulatedTelemetryProvider(sp.GetRequiredService<EngineOptions>().SamplingIntervalMs)));
app.Run();
