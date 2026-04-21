using Jiten.Core;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using StackExchange.Redis;

namespace Jiten.Cli;

public class CliContext
{
    public DbContextOptions<JitenDbContext> DbOptions { get; }
    public IDbContextFactory<JitenDbContext> ContextFactory { get; }
    public IConfigurationRoot Configuration { get; }
    public bool StoreRawText { get; }

    public CliContext(
        DbContextOptions<JitenDbContext> dbOptions,
        IDbContextFactory<JitenDbContext> contextFactory,
        IConfigurationRoot configuration,
        bool storeRawText)
    {
        DbOptions = dbOptions;
        ContextFactory = contextFactory;
        Configuration = configuration;
        StoreRawText = storeRawText;
    }

    private class SimpleDbContextFactory : IDbContextFactory<JitenDbContext>
    {
        private readonly DbContextOptions<JitenDbContext> _options;

        public SimpleDbContextFactory(DbContextOptions<JitenDbContext> options)
        {
            _options = options;
        }

        public JitenDbContext CreateDbContext()
        {
            return new JitenDbContext(_options);
        }
    }

    public static CliContext Create()
    {
        var configuration = new ConfigurationBuilder()
            .SetBasePath(Directory.GetCurrentDirectory())
            .AddJsonFile(Path.Combine("Shared", "sharedsettings.json"), optional: true, reloadOnChange: true)
            .AddJsonFile("sharedsettings.json", optional: true, reloadOnChange: true)
            .AddJsonFile("appsettings.json", optional: true, reloadOnChange: true)
            .AddEnvironmentVariables()
            .Build();

        var connectionString = configuration.GetConnectionString("JitenDatabase");
        var optionsBuilder = new DbContextOptionsBuilder<JitenDbContext>();
        optionsBuilder.UseNpgsql(connectionString, o => { o.UseQuerySplittingBehavior(QuerySplittingBehavior.SplitQuery); });

        var dbOptions = optionsBuilder.Options;
        var contextFactory = new SimpleDbContextFactory(dbOptions);
        var storeRawText = configuration.GetValue<bool>("StoreRawText");

        return new CliContext(dbOptions, contextFactory, configuration, storeRawText);
    }

    public async Task FlushRedisAsync()
    {
        var conn = Configuration.GetConnectionString("Redis");
        if (string.IsNullOrEmpty(conn))
        {
            Console.WriteLine("Redis connection string not found in configuration.");
            return;
        }

        try
        {
            using var mx = await ConnectionMultiplexer.ConnectAsync(conn);
            mx.GetDatabase().Execute("FLUSHALL");
            Console.WriteLine("Redis cache flushed successfully.");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Failed to flush Redis cache: {ex.Message}");
        }
    }
}
