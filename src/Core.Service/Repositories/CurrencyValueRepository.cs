using System;
using System.Collections.Generic;
using System.Data;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

using Core.Service.Data;
using Core.Service.Entities;
using Core.Service.Interfaces;
using Core.Service.Models;

using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;

namespace Core.Service.Repositories;

public class CurrencyValueRepository : ICurrencyValueRepository
{
    // SQL Server allows at most 2100 parameters per statement and each row uses 4. The daily ECB feed has ~30 rates.
    public const int MaxRatesPerMerge = 500;

    // Must match CurrencyValueConfiguration, and the unique (CurrencyCode, RateDate) index the ON clause relies on.
    // HOLDLOCK keeps the key range locked between the match and the insert, so two concurrent merges can't both insert
    // the same (CurrencyCode, RateDate) and fail on the unique index.
    private const string MergeHead = """
        MERGE INTO [CurrencyValues] WITH (HOLDLOCK) AS target
        USING (VALUES
        """;

    private const string MergeTail = """

        ) AS source ([CurrencyCode], [Rate], [RateDate], [UpdatedAt])
        ON target.[CurrencyCode] = source.[CurrencyCode] AND target.[RateDate] = source.[RateDate]
        WHEN MATCHED AND target.[Rate] <> source.[Rate] THEN
            UPDATE SET target.[Rate] = source.[Rate], target.[UpdatedAt] = source.[UpdatedAt]
        WHEN NOT MATCHED BY TARGET THEN
            INSERT ([CurrencyCode], [Rate], [RateDate], [UpdatedAt])
            VALUES (source.[CurrencyCode], source.[Rate], source.[RateDate], source.[UpdatedAt])
        OUTPUT $action AS [Value];
        """;

    private readonly SystemDbContext _dbContext;

    public CurrencyValueRepository(SystemDbContext dbContext)
    {
        _dbContext = dbContext;
    }

    public async Task<IReadOnlyDictionary<string, decimal>> GetLatestRatesAsync(CancellationToken cancellationToken = default)
    {
        // A currency the ECB stops publishing keeps its last known rate, so pick the latest date per currency
        // rather than the latest date overall. Served by the (CurrencyCode, RateDate) unique index.
        var latestRates = await _dbContext.CurrencyValues
            .AsNoTracking()
            .Where(c => c.RateDate == _dbContext.CurrencyValues
                .Where(other => other.CurrencyCode == c.CurrencyCode)
                .Max(other => other.RateDate))
            .Select(c => new { c.CurrencyCode, c.Rate })
            .ToListAsync(cancellationToken);

        return latestRates.ToDictionary(c => c.CurrencyCode, c => c.Rate, StringComparer.OrdinalIgnoreCase);
    }

    public async Task<CurrencyRatesMergeResult> MergeRatesAsync(IReadOnlyCollection<CurrencyValue> rates, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(rates);

        if (rates.Count == 0) return new CurrencyRatesMergeResult(0, 0);

        if (rates.Count > MaxRatesPerMerge)
            throw new ArgumentException($"At most {MaxRatesPerMerge} rates can be merged in one statement; received {rates.Count}.", nameof(rates));

        var (sql, parameters) = BuildMerge(rates);

        // One row per affected record: "INSERT" or "UPDATE". Unchanged rates match no WHEN clause and return nothing.
        // Re-running the statement is harmless, so the retrying execution strategy may safely repeat it.
        var actions = await _dbContext.Database
            .SqlQueryRaw<string>(sql, parameters)
            .ToListAsync(cancellationToken);

        return new CurrencyRatesMergeResult(
            Inserted: actions.Count(action => action == "INSERT"),
            Updated: actions.Count(action => action == "UPDATE"));
    }

    // Values are never concatenated into the SQL text: it only contains generated parameter names (@c0, @r0, ...),
    // and every value travels as a typed parameter matching its column
    private static (string Sql, object[] Parameters) BuildMerge(IReadOnlyCollection<CurrencyValue> rates)
    {
        var sql = new StringBuilder(MergeHead);
        var parameters = new List<object>(rates.Count * 4);
        var index = 0;

        foreach (var rate in rates)
        {
            var suffix = index.ToString(CultureInfo.InvariantCulture);
            sql.Append(index == 0 ? "\n    " : ",\n    ")
               .Append("(@c").Append(suffix)
               .Append(", @r").Append(suffix)
               .Append(", @d").Append(suffix)
               .Append(", @u").Append(suffix).Append(')');

            parameters.Add(new SqlParameter("@c" + suffix, SqlDbType.Char, 3) { Value = rate.CurrencyCode });
            parameters.Add(new SqlParameter("@r" + suffix, SqlDbType.Decimal) { Precision = 18, Scale = 6, Value = rate.Rate });
            parameters.Add(new SqlParameter("@d" + suffix, SqlDbType.Date) { Value = rate.RateDate.Date });
            parameters.Add(new SqlParameter("@u" + suffix, SqlDbType.DateTime2) { Value = rate.UpdatedAt });

            index++;
        }

        sql.Append(MergeTail);
        return (sql.ToString(), parameters.ToArray());
    }
}
