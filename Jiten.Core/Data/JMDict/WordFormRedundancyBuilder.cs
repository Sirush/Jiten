using Microsoft.EntityFrameworkCore;

namespace Jiten.Core.Data.JMDict;

/// <summary>Materialises <see cref="RedundancyGraphHelper"/> edges into <c>jmdict.WordFormRedundancies</c> so the coverage SQL and the roadmap loader count the same redundant forms the in-process sibling cache does.</summary>
public static class WordFormRedundancyBuilder
{
    public static async Task<int> Build(IDbContextFactory<JitenDbContext> contextFactory)
    {
        var rows = new List<JmDictWordFormRedundancy>();

        await using (var readContext = await contextFactory.CreateDbContextAsync())
        {
            readContext.Database.SetCommandTimeout(600);
            var forms = await readContext.WordForms.AsNoTracking()
                                         .OrderBy(f => f.WordId)
                                         .Select(f => new JmDictWordForm
                                         {
                                             WordId = f.WordId, ReadingIndex = f.ReadingIndex, Text = f.Text,
                                             RubyText = f.RubyText, FormType = f.FormType
                                         })
                                         .ToListAsync();

            var current = new List<JmDictWordForm>();
            void Flush()
            {
                if (current.Count > 1)
                {
                    RubyTextHelper.EnrichForms(current);
                    foreach (var (source, target) in RedundancyGraphHelper.BuildEdges(current))
                        rows.Add(new JmDictWordFormRedundancy
                        {
                            WordId = current[0].WordId, SourceReadingIndex = source, TargetReadingIndex = target
                        });
                }
                current = new List<JmDictWordForm>(2);
            }

            foreach (var form in forms)
            {
                if (current.Count > 0 && current[0].WordId != form.WordId) Flush();
                current.Add(form);
            }
            Flush();
        }

        // Delete and inserts share one transaction: a crash must not leave production with an empty table.
        await using var writeContext = await contextFactory.CreateDbContextAsync();
        writeContext.Database.SetCommandTimeout(600);
        await using var transaction = await writeContext.Database.BeginTransactionAsync();

        await writeContext.WordFormRedundancies.ExecuteDeleteAsync();

        const int batch = 10000;
        for (var i = 0; i < rows.Count; i += batch)
        {
            writeContext.WordFormRedundancies.AddRange(rows.Skip(i).Take(batch));
            await writeContext.SaveChangesAsync();
            writeContext.ChangeTracker.Clear();
        }

        await transaction.CommitAsync();
        return rows.Count;
    }
}
