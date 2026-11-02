using AeroScience.Organization;
using Npgsql;
using Xunit;

namespace AeroScience.Tests;

public sealed class RecoveryCleanupTests
{
    [Theory]
    [InlineData("postgres")]
    [InlineData("aeroscience_p17_original")]
    [InlineData("aeroscience_recovery_aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa")]
    [InlineData("aeroscience_recovery_aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa;DROP DATABASE postgres")]
    public async Task OnlyExactGeneratedVerificationNamesCanReachCleanup(string database)
    {
        await Assert.ThrowsAsync<InvalidOperationException>(()=>OrganizationRecoveryEngine.DropVerificationDatabase("not a connection string","not a connection string",database,default));
    }

    [Fact]
    public async Task AnActiveRestoredDatabaseCannotBeMistakenForADisposableVerification()
    {
        const string database="aeroscience_recovery_aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
        await Assert.ThrowsAsync<InvalidOperationException>(()=>OrganizationRecoveryEngine.DropVerificationDatabase($"Host=127.0.0.1;Database={database};Username=application","Host=127.0.0.1;Database=postgres;Username=recovery",database,default));
        await Assert.ThrowsAsync<InvalidOperationException>(()=>OrganizationRecoveryEngine.DropVerificationDatabase("Host=127.0.0.1;Database=original;Username=application",$"Host=127.0.0.1;Database={database};Username=recovery",database,default));
    }

    [PostgresFact]
    public async Task ScopedCleanupTerminatesOnlyCandidateLoginsWithoutBroadSignalRightsAndRefusesForeignPrivilegedSessions()
    {
        var connection=System.Environment.GetEnvironmentVariable("AEROSCIENCE_ORG_TEST_DATABASE")!;
        var database="aeroscience_recovery_"+Guid.NewGuid().ToString("N");var application="cleanup_app_"+Guid.NewGuid().ToString("N");var recovery="cleanup_rec_"+Guid.NewGuid().ToString("N");
        var password=Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32));
        await using var original=new NpgsqlConnection(new NpgsqlConnectionStringBuilder(connection){Pooling=false}.ConnectionString);await original.OpenAsync();
        async Task Execute(string sql){await using var command=new NpgsqlCommand(sql,original);await command.ExecuteNonQueryAsync();}
        async Task<bool> Exists(){await using var command=new NpgsqlCommand("SELECT EXISTS(SELECT FROM pg_database WHERE datname=@database)",original);command.Parameters.AddWithValue("database",database);return (bool)(await command.ExecuteScalarAsync())!;}
        var applicationConnection=new NpgsqlConnectionStringBuilder(connection){Username=application,Password=password,Pooling=false}.ConnectionString;
        var recoveryConnection=new NpgsqlConnectionStringBuilder(connection){Database="postgres",Username=recovery,Password=password,Pooling=false}.ConnectionString;
        try{
            await Execute($"CREATE ROLE \"{application}\" LOGIN NOSUPERUSER NOCREATEDB NOCREATEROLE PASSWORD '{password}'; CREATE ROLE \"{recovery}\" LOGIN NOSUPERUSER CREATEDB NOCREATEROLE PASSWORD '{password}'; GRANT \"{application}\" TO \"{recovery}\" WITH INHERIT FALSE, SET TRUE;");
            await Execute($"CREATE DATABASE \"{database}\" OWNER \"{application}\" TEMPLATE template0");
            await Execute("CREATE TEMP TABLE recovery_cleanup_original(value text); INSERT INTO recovery_cleanup_original VALUES ('original unchanged')");
            await using var recoveryPeer=new NpgsqlConnection(new NpgsqlConnectionStringBuilder(recoveryConnection){Database=database}.ConnectionString);await recoveryPeer.OpenAsync();
            await using(var role=new NpgsqlCommand($"SET ROLE \"{application}\"",recoveryPeer))await role.ExecuteNonQueryAsync();
            await using var applicationPeer=new NpgsqlConnection(new NpgsqlConnectionStringBuilder(applicationConnection){Database=database}.ConnectionString);await applicationPeer.OpenAsync();
            // Reproduce the old cleanup deterministically: an idle pooled LOGIN
            // still belongs to recovery after SET ROLE, so application cannot kill it.
            await using(var oldCleanup=new NpgsqlConnection(recoveryConnection)){
                await oldCleanup.OpenAsync();await using(var role=new NpgsqlCommand($"SET ROLE \"{application}\"",oldCleanup))await role.ExecuteNonQueryAsync();
                await using var drop=new NpgsqlCommand($"DROP DATABASE \"{database}\" WITH (FORCE)",oldCleanup);
                Assert.Equal("42501",(await Assert.ThrowsAsync<PostgresException>(()=>drop.ExecuteNonQueryAsync())).SqlState);
            }
            Assert.True(await Exists());
            await using(var privilegedPeer=new NpgsqlConnection(new NpgsqlConnectionStringBuilder(connection){Database=database,Pooling=false}.ConnectionString)){
                await privilegedPeer.OpenAsync();await using var pid=new NpgsqlCommand("SELECT pg_backend_pid()",privilegedPeer);var privilegedPid=(int)(await pid.ExecuteScalarAsync())!;
                Assert.Equal("42501",(await Assert.ThrowsAsync<PostgresException>(()=>OrganizationRecoveryEngine.DropVerificationDatabase(applicationConnection,recoveryConnection,database,default))).SqlState);
                Assert.True(await Exists()); // No silent success when a foreign privileged login prevents cleanup.
                await using var release=new NpgsqlCommand("SELECT pg_terminate_backend(@pid,5000)",original);release.Parameters.AddWithValue("pid",privilegedPid);Assert.True((bool)(await release.ExecuteScalarAsync())!);
            }
            await OrganizationRecoveryEngine.DropVerificationDatabase(applicationConnection,recoveryConnection,database,default);
            Assert.False(await Exists());
            await using(var unchanged=new NpgsqlCommand("SELECT value FROM recovery_cleanup_original",original))Assert.Equal("original unchanged",await unchanged.ExecuteScalarAsync());
            await using(var rights=new NpgsqlCommand("SELECT rolsuper,rolcreaterole,pg_has_role(oid,'pg_signal_backend','MEMBER') FROM pg_roles WHERE rolname=@role",original)){
                rights.Parameters.AddWithValue("role",recovery);await using var reader=await rights.ExecuteReaderAsync();Assert.True(await reader.ReadAsync());Assert.False(reader.GetBoolean(0));Assert.False(reader.GetBoolean(1));Assert.False(reader.GetBoolean(2));
            }
        }finally{
            await Execute($"DROP DATABASE IF EXISTS \"{database}\" WITH (FORCE)");
            await Execute($"DROP ROLE IF EXISTS \"{recovery}\"; DROP ROLE IF EXISTS \"{application}\"");
        }
    }
}
