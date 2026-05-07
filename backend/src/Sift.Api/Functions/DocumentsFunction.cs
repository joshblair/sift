using System.Text.Json;
using Amazon.Lambda.APIGatewayEvents;
using Amazon.Lambda.Core;
using Amazon.Lambda.Serialization.SystemTextJson;
using Microsoft.Extensions.DependencyInjection;
using Sift.Api.Infrastructure;
using Sift.Api.Models;
using Sift.Api.Services;

[assembly: LambdaSerializer(typeof(DefaultLambdaJsonSerializer))]

namespace Sift.Api.Functions;

public class DocumentsFunction
{
    private readonly IServiceProvider _services;

    public DocumentsFunction()
    {
        _services = Startup.BuildServiceProvider();
    }

    public async Task<APIGatewayHttpApiV2ProxyResponse> FunctionHandler(
        APIGatewayHttpApiV2ProxyRequest request, ILambdaContext context)
    {
        try
        {
            var tenantId = GetTenantId(request);
            var userId   = GetUserId(request);
            var method   = request.RequestContext.Http.Method.ToUpper();
            var path     = request.RawPath;

            await using var scope = _services.CreateAsyncScope();
            var svc = scope.ServiceProvider.GetRequiredService<IDocumentService>();

            return (method, path) switch
            {
                ("GET",    "/documents")                => await HandleList(svc, tenantId),
                ("POST",   "/documents/upload-url")     => await HandleUploadUrl(svc, request, tenantId, userId),
                ("GET",    _) when IsDocPath(path)      => await HandleGet(svc, tenantId, DocId(path)),
                ("DELETE", _) when IsDocPath(path)      => await HandleDelete(svc, tenantId, DocId(path)),
                ("OPTIONS", _)                          => ApiResponse.NoContent(),
                _                                       => ApiResponse.NotFound()
            };
        }
        catch (Exception ex)
        {
            context.Logger.LogError($"DocumentsFunction error: {ex}");
            return ApiResponse.ServerError();
        }
    }

    private static async Task<APIGatewayHttpApiV2ProxyResponse> HandleList(
        IDocumentService svc, Guid tenantId)
    {
        var docs = await svc.ListAsync(tenantId);
        return ApiResponse.Ok(docs);
    }

    private static async Task<APIGatewayHttpApiV2ProxyResponse> HandleUploadUrl(
        IDocumentService svc, APIGatewayHttpApiV2ProxyRequest request,
        Guid tenantId, Guid userId)
    {
        var body = JsonSerializer.Deserialize<UploadUrlRequest>(request.Body ?? "{}",
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

        if (body is null || string.IsNullOrWhiteSpace(body.Filename) || string.IsNullOrWhiteSpace(body.FileType))
            return ApiResponse.BadRequest("filename and fileType are required");

        var validTypes = new[] { "pdf", "docx", "csv", "txt" };
        if (!validTypes.Contains(body.FileType.ToLower()))
            return ApiResponse.BadRequest($"fileType must be one of: {string.Join(", ", validTypes)}");

        var result = await svc.CreatePresignedUploadUrlAsync(tenantId, userId, body.Filename, body.FileType.ToLower());
        return ApiResponse.Created(result);
    }

    private static async Task<APIGatewayHttpApiV2ProxyResponse> HandleGet(
        IDocumentService svc, Guid tenantId, Guid docId)
    {
        var doc = await svc.GetAsync(tenantId, docId);
        return doc is null ? ApiResponse.NotFound() : ApiResponse.Ok(doc);
    }

    private static async Task<APIGatewayHttpApiV2ProxyResponse> HandleDelete(
        IDocumentService svc, Guid tenantId, Guid docId)
    {
        await svc.DeleteAsync(tenantId, docId);
        return ApiResponse.NoContent();
    }

    private static Guid GetTenantId(APIGatewayHttpApiV2ProxyRequest request)
    {
        var claim = request.RequestContext.Authorizer.Jwt.Claims["tenantId"];
        return Guid.Parse(claim);
    }

    private static Guid GetUserId(APIGatewayHttpApiV2ProxyRequest request)
    {
        // The user's internal DB ID is stored as a custom claim after first login.
        // Falls back to Guid.Empty for the upload URL flow where the user ensures
        // their DB record exists via TenantFunction on first sign-in.
        if (request.RequestContext.Authorizer.Jwt.Claims.TryGetValue("userId", out var id)
            && Guid.TryParse(id, out var userId))
            return userId;
        return Guid.Empty;
    }

    private static bool IsDocPath(string path) =>
        path.StartsWith("/documents/") && path.Length > "/documents/".Length;

    private static Guid DocId(string path) =>
        Guid.Parse(path.Split('/')[^1]);
}
