using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using Microsoft.Extensions.Configuration;
using Victor.Models;

namespace Victor.Migrator;

// Used by 'dotnet ef migrations' at design time only.
// Connection string resolved from DATABASE_URL env var, then ConfigFiles/config.yaml, then localhost fallback.
public class VictorDbContextFactory : IDesignTimeDbContextFactory<VictorDbContext>
{
    public VictorDbContext CreateDbContext(string[] args)
    {
        var config = new ConfigurationBuilder()
            .AddYamlFile(Path.Combine(AppContext.BaseDirectory, "ConfigFiles/config.yaml"), optional: true)
            .Build();

        var connectionString = Environment.GetEnvironmentVariable("DATABASE_URL")
            ?? config["database:connectionString"]
            ?? "Host=localhost;Port=5432;Database=victor;Username=victor;Password=changeme";

        var options = new DbContextOptionsBuilder<VictorDbContext>()
            .UseNpgsql(connectionString, npgsql =>
            {
                npgsql.UseVector();
                npgsql.MigrationsAssembly(typeof(VictorDbContextFactory).Assembly.GetName().Name!);
            })
            .Options;

        return new VictorDbContext(options);
    }
}
