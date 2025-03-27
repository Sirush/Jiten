using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using Microsoft.Extensions.Configuration;
using System.IO;
using Jiten.Core;

public class JitenDbContextFactory : IDesignTimeDbContextFactory<JitenDbContext>
{
    public JitenDbContext CreateDbContext(string[] args)
    {
        var configuration = new ConfigurationBuilder()
                            .SetBasePath(Directory.GetCurrentDirectory())
                            .AddJsonFile("sharedsettings.json", optional: true, reloadOnChange: true)
                            .AddJsonFile("appsettings.json", optional: true, reloadOnChange: true)
                            .AddEnvironmentVariables()
                            .Build();

        var connectionString = configuration.GetConnectionString("JitenDatabase");
        var optionsBuilder = new DbContextOptionsBuilder<JitenDbContext>();
        optionsBuilder.UseNpgsql(connectionString, o => { o.UseQuerySplittingBehavior(QuerySplittingBehavior.SplitQuery); });

        return new JitenDbContext(optionsBuilder.Options);
    }
}