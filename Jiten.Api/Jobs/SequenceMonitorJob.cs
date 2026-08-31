using Hangfire;
using Jiten.Api.Services;
using Jiten.Core;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Jiten.Api.Jobs;

/// <summary>Alerts while there is still time to widen a column: ingestion halts hard once an int-backed sequence crosses 2^31.</summary>
public class SequenceMonitorJob(
    IDbContextFactory<JitenDbContext> jitenFactory,
    IDbContextFactory<UserDbContext> userFactory,
    IBillingAlertService alerts)
{
    private const long Threshold = 1_600_000_000;

    [Queue("default")]
    public async Task CheckSequences()
    {
        var findings = new List<string>();

        await using (var context = await jitenFactory.CreateDbContextAsync())
            findings.AddRange(await CheckAsync(context));
        await using (var context = await userFactory.CreateDbContextAsync())
            findings.AddRange(await CheckAsync(context));

        if (findings.Count > 0)
            await alerts.RaiseAsync("sequence-overflow",
                                    "Integer id sequence nearing overflow",
                                    string.Join("\n", findings.Distinct()));
    }

    private static async Task<List<string>> CheckAsync(DbContext context)
    {
        if (context.Database.ProviderName?.Contains("Npgsql") != true) return [];

        var conn = (NpgsqlConnection)context.Database.GetDbConnection();
        await conn.OpenAsync();

        await using var cmd = new NpgsqlCommand(@"
            SELECT n.nspname || '.' || c.relname || '.' || a.attname,
                   pg_sequence_last_value(s.oid)
            FROM pg_class s
            JOIN pg_depend d ON d.objid = s.oid
                            AND d.classid = 'pg_class'::regclass
                            AND d.refclassid = 'pg_class'::regclass
                            AND d.deptype IN ('a', 'i')
            JOIN pg_class c ON c.oid = d.refobjid
            JOIN pg_attribute a ON a.attrelid = c.oid AND a.attnum = d.refobjsubid
            JOIN pg_namespace n ON n.oid = c.relnamespace
            WHERE s.relkind = 'S'
              AND a.atttypid = 'int4'::regtype
              AND pg_sequence_last_value(s.oid) > @threshold", conn);
        cmd.Parameters.AddWithValue("threshold", Threshold);

        var findings = new List<string>();
        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            var column = reader.GetString(0);
            var lastValue = reader.GetInt64(1);
            findings.Add($"{column}: {lastValue:N0} ({lastValue * 100.0 / int.MaxValue:F1}% of int range)");
        }

        return findings;
    }
}
