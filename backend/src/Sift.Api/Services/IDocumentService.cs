using Sift.Api.Models;

namespace Sift.Api.Services;

public interface IDocumentService
{
    Task<List<Document>> ListAsync(Guid tenantId);
    Task<Document?> GetAsync(Guid tenantId, Guid documentId);
    Task<UploadUrlResponse> CreatePresignedUploadUrlAsync(Guid tenantId, Guid userId, string filename, string fileType);
    Task DeleteAsync(Guid tenantId, Guid documentId);
}
