using Npgsql;
using Sift.Api.Infrastructure;
using Sift.Api.Models;

namespace Sift.Api.Services;

public class TenantService(DbConnectionFactory db) : ITenantService
{
    public async Task<Tenant?> GetTenantAsync(Guid tenantId)
    {
        await using var conn = await db.CreateAsync();

        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT id, name, slug, created_at FROM tenants WHERE id = $1";
        cmd.Parameters.AddWithValue(tenantId);

        await using var reader = await cmd.ExecuteReaderAsync();
        if (!await reader.ReadAsync()) return null;

        return new Tenant
        {
            Id        = reader.GetGuid(0),
            Name      = reader.GetString(1),
            Slug      = reader.GetString(2),
            CreatedAt = reader.GetDateTime(3)
        };
    }

    public async Task<List<User>> ListUsersAsync(Guid tenantId)
    {
        await using var conn = await db.CreateAsync();
        await TenantContext.SetAsync(conn, tenantId);

        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT id, tenant_id, cognito_sub, email, role, created_at
            FROM users
            ORDER BY created_at
            """;

        var users = new List<User>();
        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
            users.Add(MapUser(reader));

        return users;
    }

    public async Task<User> EnsureUserExistsAsync(Guid tenantId, string cognitoSub, string email)
    {
        await using var conn = await db.CreateAsync();
        await TenantContext.SetAsync(conn, tenantId);

        // Upsert: insert if not exists, return existing row if already present.
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO users (tenant_id, cognito_sub, email)
            VALUES ($1, $2, $3)
            ON CONFLICT (cognito_sub) DO UPDATE SET email = EXCLUDED.email
            RETURNING id, tenant_id, cognito_sub, email, role, created_at
            """;
        cmd.Parameters.AddWithValue(tenantId);
        cmd.Parameters.AddWithValue(cognitoSub);
        cmd.Parameters.AddWithValue(email);

        await using var reader = await cmd.ExecuteReaderAsync();
        await reader.ReadAsync();
        return MapUser(reader);
    }

    public async Task UpdateUserRoleAsync(Guid tenantId, Guid userId, string role)
    {
        await using var conn = await db.CreateAsync();
        await TenantContext.SetAsync(conn, tenantId);

        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "UPDATE users SET role = $1 WHERE id = $2";
        cmd.Parameters.AddWithValue(role);
        cmd.Parameters.AddWithValue(userId);
        await cmd.ExecuteNonQueryAsync();
    }

    private static User MapUser(NpgsqlDataReader r) => new()
    {
        Id         = r.GetGuid(0),
        TenantId   = r.GetGuid(1),
        CognitoSub = r.GetString(2),
        Email      = r.GetString(3),
        Role       = r.GetString(4),
        CreatedAt  = r.GetDateTime(5)
    };
}
