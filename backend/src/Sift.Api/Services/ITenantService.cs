using Sift.Api.Models;

namespace Sift.Api.Services;

public interface ITenantService
{
    Task<Tenant?> GetTenantAsync(Guid tenantId);
    Task<List<User>> ListUsersAsync(Guid tenantId);
    Task<User> EnsureUserExistsAsync(Guid tenantId, string cognitoSub, string email);
    Task UpdateUserRoleAsync(Guid tenantId, Guid userId, string role);
}
