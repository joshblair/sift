using System.Text.Json;
using Amazon.Lambda.APIGatewayEvents;
using Amazon.Lambda.Core;
using Microsoft.Extensions.DependencyInjection;
using Sift.Api.Infrastructure;
using Sift.Api.Models;
using Sift.Api.Services;

namespace Sift.Api.Functions;

public class ChatFunction
{
    private readonly IServiceProvider _services;

    public ChatFunction()
    {
        _services = Startup.BuildServiceProvider();
    }

    public async Task<APIGatewayHttpApiV2ProxyResponse> FunctionHandler(
        APIGatewayHttpApiV2ProxyRequest request, ILambdaContext context)
    {
        try
        {
            var method = request.RequestContext.Http.Method.ToUpper();
            if (method == "OPTIONS") return ApiResponse.NoContent();
            if (method != "POST")   return ApiResponse.NotFound();

            var tenantId = Guid.Parse(request.RequestContext.Authorizer.Jwt.Claims["tenantId"]);

            var body = JsonSerializer.Deserialize<ChatRequest>(request.Body ?? "{}",
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

            if (body is null || string.IsNullOrWhiteSpace(body.Question))
                return ApiResponse.BadRequest("question is required");

            await using var scope = _services.CreateAsyncScope();
            var svc = scope.ServiceProvider.GetRequiredService<IChatService>();

            var response = await svc.QueryAsync(tenantId, body.Question);
            return ApiResponse.Ok(response);
        }
        catch (Exception ex)
        {
            context.Logger.LogError($"ChatFunction error: {ex}");
            return ApiResponse.ServerError();
        }
    }
}
