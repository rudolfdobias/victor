using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Victor.Models;

var config = new ConfigurationBuilder()
    .AddYamlFile(Path.Combine(AppContext.BaseDirectory, "ConfigFiles/config.yaml"), optional: true)
    .Build();

var connectionString = Environment.GetEnvironmentVariable("DATABASE_URL")
    ?? config["database:connectionString"]
    ?? throw new InvalidOperationException(
        "Connection string is required. Set DATABASE_URL env var or database:connectionString in ConfigFiles/config.yaml.");

var options = new DbContextOptionsBuilder<VictorDbContext>()
    .UseNpgsql(connectionString, npgsql =>
    {
        npgsql.UseVector();
        npgsql.MigrationsAssembly(typeof(Program).Assembly.GetName().Name!);
    })
    .Options;

await using var context = new VictorDbContext(options);

Console.WriteLine("Applying migrations...");
await context.Database.MigrateAsync();
Console.WriteLine("Done.");
