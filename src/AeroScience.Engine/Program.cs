using AeroScience.Engine;

if (ScientificHdfWorker.IsCommand(args)) Environment.ExitCode=ScientificHdfWorker.Run();
else EngineApplication.Create(args).Run();

public partial class Program { }
