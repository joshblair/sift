using Sift.Api.Models;

namespace Sift.Api.Services;

public interface IChatService
{
    Task<ChatResponse> QueryAsync(Guid tenantId, string question);
}
