using System.Text.Json;
using Amazon.SecretsManager;
using Amazon.SecretsManager.Model;
using Npgsql;

namespace Sift.Api.Infrastructure;

public class DbConnectionFactory
{
    private readonly string _secretArn;
    private readonly string _host;
    private readonly int    _port;
    private readonly string _database;

    // Cache resolved credentials so we hit Secrets Manager once per warm Lambda container.
    private string? _cachedConnString;
    private DateTime _cacheExpiry = DateTime.MinValue;

    public DbConnectionFactory()
    {
        _secretArn = Env("DB_SECRET_ARN");
        _host      = Env("DB_HOST");
        _port      = int.Parse(Environment.GetEnvironmentVariable("DB_PORT") ?? "5432");
        _database  = Environment.GetEnvironmentVariable("DB_NAME") ?? "sift";
    }

    public async Task<NpgsqlConnection> CreateAsync()
    {
        var connString = await GetConnectionStringAsync();
        var conn = new NpgsqlConnection(connString);
        await conn.OpenAsync();
        return conn;
    }

    private async Task<string> GetConnectionStringAsync()
    {
        if (_cachedConnString is not null && DateTime.UtcNow < _cacheExpiry)
            return _cachedConnString;

        using var client = new AmazonSecretsManagerClient();
        var response = await client.GetSecretValueAsync(new GetSecretValueRequest
        {
            SecretId = _secretArn
        });

        var secret = JsonSerializer.Deserialize<DbSecret>(response.SecretString)
            ?? throw new InvalidOperationException("Could not parse DB secret");

        _cachedConnString = new NpgsqlConnectionStringBuilder
        {
            Host               = _host,
            Port               = _port,
            Database           = _database,
            Username           = secret.Username,
            Password           = secret.Password,
            SslMode            = SslMode.Require,
            TrustServerCertificate = true,
            Pooling            = false   // Lambda: no persistent connection pool
        }.ConnectionString;

        _cacheExpiry = DateTime.UtcNow.AddMinutes(5);
        return _cachedConnString;
    }

    private static string Env(string name) =>
        Environment.GetEnvironmentVariable(name)
        ?? throw new InvalidOperationException($"Missing environment variable: {name}");

    private sealed record DbSecret(
        [property: System.Text.Json.Serialization.JsonPropertyName("username")] string Username,
        [property: System.Text.Json.Serialization.JsonPropertyName("password")] string Password);
}
