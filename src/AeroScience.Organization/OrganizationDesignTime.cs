using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace AeroScience.Organization;

// Migration tooling only: model generation never needs credentials or opens a database.
public sealed class OrganizationDesignTime:IDesignTimeDbContextFactory<OrganizationDatabase>
{
    public OrganizationDatabase CreateDbContext(string[] args)=>new(new DbContextOptionsBuilder<OrganizationDatabase>().UseNpgsql().Options);
}
