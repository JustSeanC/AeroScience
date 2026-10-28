using System.Text.RegularExpressions;
using Npgsql;
namespace AeroScience.TestHost;

// Disposable PostgreSQL test database lifecycle, never included in a production application.
internal static class OrganizationFixture
{
    public static async Task Run(string[] args)
    {
        if(args.Length!=2||args[0] is not("create" or "drop")||!Regex.IsMatch(args[1],"^aeroscience_p13_[a-f0-9]{32}$",RegexOptions.CultureInvariant,TimeSpan.FromSeconds(1)))throw new ArgumentException("Choose an isolated organization fixture identity.");
        var connection=new NpgsqlConnectionStringBuilder(Environment.GetEnvironmentVariable("AEROSCIENCE_ORG_TEST_DATABASE")??throw new InvalidOperationException("An isolated PostgreSQL fixture is required."));
        if(connection.Host is not("localhost" or "127.0.0.1" or "::1"))throw new InvalidOperationException("Organization fixtures only access an explicit loopback test server.");
        await using var db=new NpgsqlConnection(connection.ConnectionString);await db.OpenAsync();
        await using var command=new NpgsqlCommand(args[0]=="create"?$"CREATE DATABASE \"{args[1]}\" ENCODING 'UTF8' TEMPLATE template0":$"DROP DATABASE IF EXISTS \"{args[1]}\" WITH (FORCE)",db);await command.ExecuteNonQueryAsync();
        Console.WriteLine("PASS: isolated organization fixture database "+args[0]+"; no personal records or production database modified.");
    }
}
