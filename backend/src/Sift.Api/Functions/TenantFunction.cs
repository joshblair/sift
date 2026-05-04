using System.Text.Json;
using Amazon.Lambda.APIGatewayEvents;
using Amazon.Lambda.Core;
using Microsoft.Extensions.DependencyInjection;
using Sift.Api.Infrastructure;
using Sift.Api.Models;
using Sift.Api.Services;

namespace Sift.Api.Functions;

public class TenantFunction
{
    private readonly IServiceProvider _services;

    public TenantFunction()
    {
        _services = Startup.BuildServiceProvider();
    }

    public async Task<APIGatewayHttpApiV2ProxyResponse> FunctionHandler(
        APIGatewayHttpApiV2ProxyRequest request, ILambdaContext context)
    {
        try
        {
            var tenantId   = Guid.Parse(request.RequestContext.Authorizer.Jwt.Claims["tenantId"]);
            var cognitoSub = request.RequestContext.Authorizer.Jwt.Claims["sub"];
            var email      = request.RequestContext.Authorizer.Jwt.Claims.GetValueOrDefault("email", "");
            var method     = request.RequestContext.Http.Method.ToUpper();
            var path       = request.RequestContext.Http.Path;

            await using var scope = _services.CreateAsyncScope();
            var svc = scope.ServiceProvider.GetRequiredService<ITenantService>();

            return (method, path) switch
            {
                // First-login upsert: creates user row if it doesn't exist.
                ("POST",   "/tenants/me/sync")                         => await HandleSync(svc, tenantId, cognitoSub, email),
                ("GET",    "/tenants/me")                              => await HandleGetTenant(svc, tenantId),
                ("GET",    "/tenants/users")                           => await HandleListUsers(svc, tenantId),
                ("PUT",    _) when IsUserRolePath(path)                => await HandleUpdateRole(svc, request, tenantId, UserId(path)),
                ("OPTIONS", _)                                         => ApiResponse.NoContent(),
                _                                                      => ApiResponse.NotFound()
            };
        }
        catch (Exception ex)
        {
            context.Logger.LogError($"TenantFunction error: {ex}");
            return ApiResponse.ServerError();
        }
    }

    private static async Task<APIGatewayHttpApiV2ProxyResponse> HandleSync(
        ITenantService svc, Guid tenantId, string cognitoSub, string email)
    {
        var user = await svc.EnsureUserExistsAsync(tenantId, cognitoSub, email);
        return ApiResponse.Ok(user);
    }

    private static async Task<APIGatewayHttpApiV2ProxyResponse> HandleGetTenant(
        ITenantService svc, Guid tenantId)
    {
        var tenant = await svc.GetTenantAsync(tenantId);
        return tenant is null ? ApiResponse.NotFound() : ApiResponse.Ok(tenant);
    }

    private static async Task<APIGatewayHttpApiV2ProxyResponse> HandleListUsers(
        ITenantService svc, Guid tenantId)
    {
        var users = await svc.ListUsersAsync(tenantId);
        return ApiResponse.Ok(users);
    }

    private static async Task<APIGatewayHttpApiV2ProxyResponse> HandleUpdateRole(
        ITenantService svc, APIGatewayHttpApiV2ProxyRequest request,
        Guid tenantId, Guid userId)
    {
        var body = JsonSerializer.Deserialize<UpdateRoleRequest>(request.Body ?? "{}",
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

        if (body is null || body.Role is not ("admin" or "member"))
            return ApiResponse.BadRequest("role must be 'admin' or 'member'");

        await svc.UpdateUserRoleAsync(tenantId, userId, body.Role);
        return ApiResponse.NoContent();
    }

    private static bool IsUserRolePath(string path) =>
        path.StartsWith("/tenants/users/") && path.EndsWith("/role");

    private static Guid UserId(string path)
    {
        // /tenants/users/{id}/role
        var parts = path.Split('/');
        return Guid.Parse(parts[^2]);
    }
}
