using AeroScience.Missions;
using AeroScience.Shared;
using Xunit;
namespace AeroScience.Tests;

public sealed class ProgramWorkflowTests
{
    static TestPhaseSnapshot Phase(Guid id,int sequence,Guid[] prerequisites,Guid? card=null)=>new(id,Guid.NewGuid(),1,1,new(1,"Initial conditions","Fictional organization workflow only",sequence,PhaseAdvancement.ManagerRelease,prerequisites,[new(card??Guid.NewGuid(),2,new('a',64))]),false,false,0,0,Guid.NewGuid(),"Manager","Manager",DateTimeOffset.UtcNow,"saved","Reviewed requirements",null);
    [Fact] public void PhaseGraphsRequireEarlierPrerequisitesAndDoNotSilentlyDuplicateCards()
    {
        var first=Phase(Guid.NewGuid(),1,[]);var second=Phase(Guid.NewGuid(),2,[first.Id]);ProgramWorkflowRules.Graph([first,second]);Assert.Throws<ArgumentException>(()=>ProgramWorkflowRules.Graph([first with{Definition=first.Definition with{Prerequisites=[second.Id]}},second]));Assert.Throws<ArgumentException>(()=>ProgramWorkflowRules.Graph([first,second with{Definition=second.Definition with{Requirements=first.Definition.Requirements}}]));Assert.Throws<ArgumentException>(()=>ProgramWorkflowRules.Graph([second]));
    }
    [Fact] public void PhaseCriteriaAreBoundedAndPartialCreditIsExplicitWithoutChangingFlightDefinitions()
    {
        var first=Phase(Guid.NewGuid(),1,[]);ProgramWorkflowRules.Validate(first.Definition);Assert.Throws<ArgumentException>(()=>ProgramWorkflowRules.Validate(first.Definition with{Requirements=[]}));Assert.Throws<ArgumentException>(()=>ProgramWorkflowRules.Validate(first.Definition with{Requirements=[first.Definition.Requirements[0] with{CountAuthorizedPartialContributors=true}]}));Assert.Throws<ArgumentException>(()=>ProgramWorkflowRules.Graph(Enumerable.Range(1,17).Select(n=>Phase(Guid.NewGuid(),n,[]))));Assert.False(new ProgramWorkflowPolicy().PhaseGatesEnabled);Assert.Equal(PhaseAdvancement.ManagerRelease,first.Definition.Advancement);Assert.Contains(ProgramWorkflowRules.Limitations,s=>s.Contains("P20"));
    }
}
