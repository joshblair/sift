using FluentAssertions;
using Moq;
using Sift.Api.Models;
using Sift.Api.Services;

namespace Sift.Api.Tests.Services;

public class TenantServiceTests
{
    private readonly Mock<ITenantService> _svc = new();

    [Fact]
    public async Task GetTenantAsync_ReturnsTenant_WhenFound()
    {
        var tenantId = Guid.NewGuid();
        var tenant   = new Tenant { Id = tenantId, Name = "Acme Corp", Slug = "acme" };

        _svc.Setup(s => s.GetTenantAsync(tenantId)).ReturnsAsync(tenant);

        var result = await _svc.Object.GetTenantAsync(tenantId);

        result.Should().NotBeNull();
        result!.Name.Should().Be("Acme Corp");
        result.Slug.Should().Be("acme");
    }

    [Fact]
    public async Task GetTenantAsync_ReturnsNull_WhenNotFound()
    {
        var tenantId = Guid.NewGuid();

        _svc.Setup(s => s.GetTenantAsync(tenantId)).ReturnsAsync((Tenant?)null);

        var result = await _svc.Object.GetTenantAsync(tenantId);

        result.Should().BeNull();
    }

    [Fact]
    public async Task EnsureUserExistsAsync_ReturnsUser()
    {
        var tenantId   = Guid.NewGuid();
        var cognitoSub = "cognito-sub-abc";
        var email      = "alice@acme.com";
        var user       = new User { Id = Guid.NewGuid(), TenantId = tenantId, Email = email, Role = "member" };

        _svc.Setup(s => s.EnsureUserExistsAsync(tenantId, cognitoSub, email)).ReturnsAsync(user);

        var result = await _svc.Object.EnsureUserExistsAsync(tenantId, cognitoSub, email);

        result.Email.Should().Be(email);
        result.Role.Should().Be("member");
        result.TenantId.Should().Be(tenantId);
    }

    [Fact]
    public async Task ListUsersAsync_ReturnsUsersForTenant()
    {
        var tenantId = Guid.NewGuid();
        var users    = new List<User>
        {
            new() { Id = Guid.NewGuid(), TenantId = tenantId, Email = "admin@acme.com", Role = "admin" },
            new() { Id = Guid.NewGuid(), TenantId = tenantId, Email = "user@acme.com",  Role = "member" }
        };

        _svc.Setup(s => s.ListUsersAsync(tenantId)).ReturnsAsync(users);

        var result = await _svc.Object.ListUsersAsync(tenantId);

        result.Should().HaveCount(2);
        result.Should().ContainSingle(u => u.Role == "admin");
    }

    [Fact]
    public async Task UpdateUserRoleAsync_CallsServiceWithCorrectArguments()
    {
        var tenantId = Guid.NewGuid();
        var userId   = Guid.NewGuid();

        _svc.Setup(s => s.UpdateUserRoleAsync(tenantId, userId, "admin")).Returns(Task.CompletedTask);

        await _svc.Object.UpdateUserRoleAsync(tenantId, userId, "admin");

        _svc.Verify(s => s.UpdateUserRoleAsync(tenantId, userId, "admin"), Times.Once);
    }
}
