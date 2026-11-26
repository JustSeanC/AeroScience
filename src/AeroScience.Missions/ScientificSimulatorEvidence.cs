using AeroScience.Shared;

namespace AeroScience.Missions;

public static class ScientificSimulatorEvidence
{
    public static void Validate(ScienceObservation observation)
    {
        if(observation.SimulatorOrigin is {} origin)SimulatorProvenance.Validate(origin);
        if(observation.SimulatorContext is {} context)
        {
            SimulatorProvenance.Validate(context);
            if(observation.SimulatorOrigin is null||context.ConnectionEpoch!=observation.SimulatorOrigin.ConnectionEpoch)
                throw new ArgumentException("Scientific timing evidence must match its original simulator epoch.");
        }
        if(observation.SimulatorOrigin?.SimulatorId==SimulatorPreferences.XPlane12&&observation.Provenance==ScienceProvenance.SimulatorReported
            &&!SimulatorAccessPolicy.QualifiedObservation(observation))throw new ArgumentException("X-Plane atmospheric evidence must match the original confirmed native value, units and clock basis.");
    }
}
