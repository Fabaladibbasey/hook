using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Hook.Shared.Persistence.Data;

public class HookDbContextFactory : IDesignTimeDbContextFactory<HookDbContext>
{
    public HookDbContext CreateDbContext(string[] args)
    {
        var configuration = new ConfigurationBuilder()
            .SetBasePath(Directory.GetCurrentDirectory())
            .AddJsonFile("appsettings.json", optional: false)
            .AddJsonFile("appsettings.Development.json", optional: true)
            .AddEnvironmentVariables()
            .Build();

        var connectionString = configuration.GetConnectionString("HookDb")
            ?? throw new InvalidOperationException("Connection string 'HookDb' is not configured.");

        var optionsBuilder = new DbContextOptionsBuilder<HookDbContext>();
        optionsBuilder.UseNpgsql(connectionString, npgsql =>
        {
            npgsql.UseNetTopologySuite();
            npgsql.MigrationsAssembly(typeof(HookDbContext).Assembly.GetName().Name);
        });

        return new HookDbContext(optionsBuilder.Options);
    }
}
