using Npgsql;

namespace Sift.Api.Infrastructure;

public static class TenantContext
{
    public static async Task SetAsync(NpgsqlConnection connection, Guid tenantId)
    {
        await using var cmd = connection.CreateCommand();
        // is_local=false → session scope. Safe because Lambda connections are
        // never pooled across requests (Pooling=false in DbConnectionFactory).
        cmd.CommandText = "SELECT set_config('app.current_tenant_id', $1, false)";
        cmd.Parameters.AddWithValue(tenantId.ToString());
        await cmd.ExecuteNonQueryAsync();
    }
}
