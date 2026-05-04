using FluentAssertions;
using Moq;
using Sift.Api.Models;
using Sift.Api.Services;

namespace Sift.Api.Tests.Services;

public class ChatServiceTests
{
    private readonly Mock<IChatService> _svc = new();

    [Fact]
    public async Task QueryAsync_ReturnsAnswerWithCitations()
    {
        var tenantId = Guid.NewGuid();
        var question = "What is the refund policy?";
        var response = new ChatResponse
        {
            Answer = "The refund policy allows returns within 30 days [1].",
            Citations =
            [
                new Citation
                {
                    DocumentId = Guid.NewGuid(),
                    Filename   = "policy.pdf",
                    Excerpt    = "Returns accepted within 30 days of purchase.",
                    ChunkIndex = 2
                }
            ]
        };

        _svc.Setup(s => s.QueryAsync(tenantId, question)).ReturnsAsync(response);

        var result = await _svc.Object.QueryAsync(tenantId, question);

        result.Answer.Should().Contain("30 days");
        result.Citations.Should().HaveCount(1);
        result.Citations[0].Filename.Should().Be("policy.pdf");
    }

    [Fact]
    public async Task QueryAsync_ReturnsEmptyCitations_WhenNoRelevantChunks()
    {
        var tenantId = Guid.NewGuid();

        _svc.Setup(s => s.QueryAsync(tenantId, It.IsAny<string>()))
            .ReturnsAsync(new ChatResponse
            {
                Answer    = "I could not find relevant information in your documents.",
                Citations = []
            });

        var result = await _svc.Object.QueryAsync(tenantId, "unrelated question");

        result.Citations.Should().BeEmpty();
        result.Answer.Should().NotBeNullOrWhiteSpace();
    }
}
