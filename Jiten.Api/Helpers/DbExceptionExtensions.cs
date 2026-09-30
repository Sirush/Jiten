using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Jiten.Api.Helpers;

public static class DbExceptionExtensions
{
    public static bool IsUniqueViolation(this DbUpdateException ex)
        => ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation };
}
