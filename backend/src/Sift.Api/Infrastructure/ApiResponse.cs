using System.Text.Json;
using Amazon.Lambda.APIGatewayEvents;

namespace Sift.Api.Infrastructure;

public static class RequestHelpers
{
    /// Returns the path without the stage prefix.
    /// For stage "dev", rawPath "/dev/tenants/me" → "/tenants/me".
    public static string GetPath(APIGatewayHttpApiV2ProxyRequest request)
    {
        var stage   = request.RequestContext.Stage ?? "";
        var rawPath = request.RawPath ?? "";
        var prefix  = $"/{stage}";
        return rawPath.StartsWith(prefix) ? rawPath[prefix.Length..] : rawPath;
    }
}

public static class ApiResponse
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public static APIGatewayHttpApiV2ProxyResponse Ok(object? body = null) =>
        Json(200, body);

    public static APIGatewayHttpApiV2ProxyResponse Created(object? body = null) =>
        Json(201, body);

    public static APIGatewayHttpApiV2ProxyResponse NoContent() =>
        new() { StatusCode = 204, Headers = CorsHeaders() };

    public static APIGatewayHttpApiV2ProxyResponse BadRequest(string message) =>
        Json(400, new { error = message });

    public static APIGatewayHttpApiV2ProxyResponse NotFound(string message = "Not found") =>
        Json(404, new { error = message });

    public static APIGatewayHttpApiV2ProxyResponse Forbidden() =>
        Json(403, new { error = "Forbidden" });

    public static APIGatewayHttpApiV2ProxyResponse ServerError(string message = "Internal server error") =>
        Json(500, new { error = message });

    private static APIGatewayHttpApiV2ProxyResponse Json(int statusCode, object? body) =>
        new()
        {
            StatusCode = statusCode,
            Body       = body is null ? "" : JsonSerializer.Serialize(body, JsonOpts),
            Headers    = CorsHeaders()
        };

    private static Dictionary<string, string> CorsHeaders() => new()
    {
        ["Content-Type"]                 = "application/json",
        ["Access-Control-Allow-Origin"]  = "*",
        ["Access-Control-Allow-Headers"] = "Content-Type,Authorization",
        ["Access-Control-Allow-Methods"] = "GET,POST,PUT,DELETE,OPTIONS"
    };
}
