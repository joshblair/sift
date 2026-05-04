using FluentAssertions;
using Moq;
using Sift.Api.Models;
using Sift.Api.Services;

namespace Sift.Api.Tests.Services;

public class DocumentServiceTests
{
    private readonly Mock<IDocumentService> _svc = new();

    [Fact]
    public async Task ListAsync_ReturnsDocumentsForTenant()
    {
        var tenantId = Guid.NewGuid();
        var expected = new List<Document>
        {
            new() { Id = Guid.NewGuid(), TenantId = tenantId, Filename = "report.pdf", Status = "ready" }
        };

        _svc.Setup(s => s.ListAsync(tenantId)).ReturnsAsync(expected);

        var result = await _svc.Object.ListAsync(tenantId);

        result.Should().HaveCount(1);
        result[0].TenantId.Should().Be(tenantId);
        result[0].Filename.Should().Be("report.pdf");
    }

    [Fact]
    public async Task GetAsync_ReturnsNull_WhenDocumentNotFound()
    {
        var tenantId = Guid.NewGuid();
        var docId    = Guid.NewGuid();

        _svc.Setup(s => s.GetAsync(tenantId, docId)).ReturnsAsync((Document?)null);

        var result = await _svc.Object.GetAsync(tenantId, docId);

        result.Should().BeNull();
    }

    [Fact]
    public async Task GetAsync_ReturnsDocument_WhenFound()
    {
        var tenantId = Guid.NewGuid();
        var docId    = Guid.NewGuid();
        var doc      = new Document { Id = docId, TenantId = tenantId, Status = "ready" };

        _svc.Setup(s => s.GetAsync(tenantId, docId)).ReturnsAsync(doc);

        var result = await _svc.Object.GetAsync(tenantId, docId);

        result.Should().NotBeNull();
        result!.Id.Should().Be(docId);
        result.Status.Should().Be("ready");
    }

    [Fact]
    public async Task CreatePresignedUploadUrlAsync_ReturnsUrlAndDocumentId()
    {
        var tenantId  = Guid.NewGuid();
        var userId    = Guid.NewGuid();
        var expected  = new UploadUrlResponse(Guid.NewGuid(), "https://s3.example.com/upload", "tenant/doc/file.pdf");

        _svc.Setup(s => s.CreatePresignedUploadUrlAsync(tenantId, userId, "file.pdf", "pdf"))
            .ReturnsAsync(expected);

        var result = await _svc.Object.CreatePresignedUploadUrlAsync(tenantId, userId, "file.pdf", "pdf");

        result.UploadUrl.Should().StartWith("https://");
        result.DocumentId.Should().NotBeEmpty();
        result.S3Key.Should().Contain("file.pdf");
    }

    [Fact]
    public async Task DeleteAsync_CompletesWithoutError()
    {
        var tenantId = Guid.NewGuid();
        var docId    = Guid.NewGuid();

        _svc.Setup(s => s.DeleteAsync(tenantId, docId)).Returns(Task.CompletedTask);

        var act = async () => await _svc.Object.DeleteAsync(tenantId, docId);

        await act.Should().NotThrowAsync();
        _svc.Verify(s => s.DeleteAsync(tenantId, docId), Times.Once);
    }
}
