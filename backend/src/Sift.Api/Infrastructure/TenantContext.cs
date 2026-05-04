using Npgsql;

namespace Sift.Api.Infrastructure;

public static class TenantContext
{
    /// <summary>
    /// Sets app.current_tenant_id for the duration of the current transaction.
    /// Must be called before any RLS-protected query.
    /// </summary>
    public static async Task SetAsync(NpgsqlConnection connection, Guid tenantId)
    {
        await using var cmd = connection.CreateCommand();
        // set_config with is_local=true scopes the value to the current transaction.
        cmd.CommandText = "SELECT set_config('app.current_tenant_id', $1, true)";
        cmd.Parameters.AddWithValue(tenantId.ToString());
        await cmd.ExecuteNonQueryAsync();
    }
}
