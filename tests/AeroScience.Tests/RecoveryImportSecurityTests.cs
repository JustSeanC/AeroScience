using AeroScience.Organization;
using Xunit;
namespace AeroScience.Tests;
public sealed class RecoveryImportSecurityTests
{
    static readonly Guid Organization=Guid.NewGuid();
    static OrganizationRecoveryKit Kit(string repository)=>new(1,Organization,"https://old.example.org","unused",new string('a',64),null,null,null,""){Independent=new(repository,new string('b',64),new(){["AWS_ACCESS_KEY_ID"]="fixture-only",["AWS_SECRET_ACCESS_KEY"]="fixture-only"},"path"),RecoveryPoint=new(Guid.NewGuid(),Organization,new string('c',64),new string('d',64),DateTimeOffset.UtcNow)};
    static OrganizationSettings Settings()=>new("Host=private;Database=organization;Username=application;SSL Mode=VerifyFull",new("https://new.example.org"),false,null,null,null,[]);
    [Theory]
    [InlineData("s3:http://s3.us-east-1.amazonaws.com/bucket")]
    [InlineData("s3:https://s3.us-east-1.amazonaws.com:8443/bucket")]
    [InlineData("s3:https://user:secret@s3.us-east-1.amazonaws.com/bucket")]
    [InlineData("s3:https://s3.us-east-1.amazonaws.com.attacker.example/bucket")]
    [InlineData("s3:https://127.0.0.1/bucket")]
    [InlineData("s3:https://169.254.169.254/bucket")]
    [InlineData("s3:https://internal.railway.internal/bucket")]
    [InlineData("rest:https://attacker.example/recovery")]
    [InlineData("/tmp/private-file")]
    [InlineData("s3:https://s3.us-east-1.amazonaws.com/bucket?redirect=local")]
    [InlineData("s3:https://s3.us-east-1.amazonaws.com/bucket/%2e%2e/private")]
    public void GuidedRecoveryCannotConfigureArbitraryNetworkDestinations(string repository)=>Assert.Throws<ArgumentException>(()=>OrganizationRecoveryImport.Validate(Kit(repository),Settings(),true));
    [Fact]public void StandardS3ProfileStillRequiresExplicitRelocationAndIndependentIntegrityReferences()
    {
        var kit=Kit("s3:https://s3.us-east-1.amazonaws.com/independent-organization/backups");OrganizationRecoveryImport.Validate(kit,Settings(),true);Assert.Throws<ArgumentException>(()=>OrganizationRecoveryImport.Validate(kit,Settings(),false));Assert.Throws<ArgumentException>(()=>OrganizationRecoveryImport.Validate(kit with{Independent=null},Settings(),true));Assert.Throws<ArgumentException>(()=>OrganizationRecoveryImport.Validate(kit with{RecoveryPoint=kit.RecoveryPoint! with{OrganizationId=Guid.NewGuid()}},Settings(),true));Assert.Throws<ArgumentException>(()=>OrganizationRecoveryImport.Validate(kit with{Independent=kit.Independent! with{Credentials=new(){["AWS_ENDPOINT_URL"]="http://localhost"}}},Settings(),true));
    }
}
