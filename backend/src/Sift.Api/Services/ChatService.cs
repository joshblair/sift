using System.Text;
using System.Text.Json;
using Amazon.BedrockRuntime;
using Amazon.BedrockRuntime.Model;
using Npgsql;
using Sift.Api.Infrastructure;
using Sift.Api.Models;

namespace Sift.Api.Services;

public class ChatService(DbConnectionFactory db) : IChatService
{
    private const string EmbedModelId = "amazon.titan-embed-text-v2:0";
    private const string ChatModelId  = "anthropic.claude-3-haiku-20240307-v1:0";
    private const int    TopK         = 8;

    public async Task<ChatResponse> QueryAsync(Guid tenantId, string question)
    {
        using var bedrock = new AmazonBedrockRuntimeClient(Amazon.RegionEndpoint.USWest2);

        var embedding  = await EmbedAsync(bedrock, question);
        var chunks     = await SearchChunksAsync(tenantId, embedding);
        var (answer, citations) = await GenerateAnswerAsync(bedrock, question, chunks);

        return new ChatResponse { Answer = answer, Citations = citations };
    }

    private static async Task<float[]> EmbedAsync(AmazonBedrockRuntimeClient bedrock, string text)
    {
        var payload = JsonSerializer.Serialize(new
        {
            inputText  = text,
            dimensions = 1536,
            normalize  = true
        });

        var response = await bedrock.InvokeModelAsync(new InvokeModelRequest
        {
            ModelId     = EmbedModelId,
            ContentType = "application/json",
            Accept      = "application/json",
            Body        = new MemoryStream(Encoding.UTF8.GetBytes(payload))
        });

        using var doc = await JsonDocument.ParseAsync(response.Body);
        var embeddingArray = doc.RootElement.GetProperty("embedding");

        return embeddingArray.EnumerateArray()
            .Select(e => e.GetSingle())
            .ToArray();
    }

    private async Task<List<ChunkResult>> SearchChunksAsync(Guid tenantId, float[] embedding)
    {
        // Pass embedding as a Postgres vector literal: '[0.1,0.2,...]'
        var vectorLiteral = $"[{string.Join(",", embedding)}]";

        await using var conn = await db.CreateAsync();
        await TenantContext.SetAsync(conn, tenantId);

        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT dc.id, dc.document_id, dc.chunk_index, dc.content,
                   d.filename,
                   dc.embedding <=> $1::vector AS distance
            FROM document_chunks dc
            JOIN documents d ON d.id = dc.document_id
            ORDER BY distance
            LIMIT $2
            """;
        cmd.Parameters.AddWithValue(NpgsqlTypes.NpgsqlDbType.Text, vectorLiteral);
        cmd.Parameters.AddWithValue(TopK);

        var results = new List<ChunkResult>();
        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            results.Add(new ChunkResult(
                ChunkId    : reader.GetGuid(0),
                DocumentId : reader.GetGuid(1),
                ChunkIndex : reader.GetInt32(2),
                Content    : reader.GetString(3),
                Filename   : reader.GetString(4)
            ));
        }

        return results;
    }

    private static async Task<(string Answer, List<Citation> Citations)> GenerateAnswerAsync(
        AmazonBedrockRuntimeClient bedrock, string question, List<ChunkResult> chunks)
    {
        var context = string.Join("\n\n", chunks.Select((c, i) =>
            $"[{i + 1}] From \"{c.Filename}\" (chunk {c.ChunkIndex}):\n{c.Content}"));

        var systemPrompt = """
            You are a helpful document assistant. Answer the user's question using only
            the provided document excerpts. Cite your sources using [1], [2], etc.
            If the answer cannot be found in the excerpts, say so clearly.
            """;

        var userMessage = $"Document excerpts:\n{context}\n\nQuestion: {question}";

        var payload = JsonSerializer.Serialize(new
        {
            anthropic_version = "bedrock-2023-05-31",
            max_tokens        = 1024,
            system            = systemPrompt,
            messages          = new[]
            {
                new { role = "user", content = userMessage }
            }
        });

        var response = await bedrock.InvokeModelAsync(new InvokeModelRequest
        {
            ModelId     = ChatModelId,
            ContentType = "application/json",
            Accept      = "application/json",
            Body        = new MemoryStream(Encoding.UTF8.GetBytes(payload))
        });

        using var doc    = await JsonDocument.ParseAsync(response.Body);
        var answer = doc.RootElement
            .GetProperty("content")[0]
            .GetProperty("text")
            .GetString() ?? "";

        var citations = chunks.Select(c => new Citation
        {
            DocumentId = c.DocumentId,
            Filename   = c.Filename,
            Excerpt    = c.Content[..Math.Min(200, c.Content.Length)],
            ChunkIndex = c.ChunkIndex
        }).ToList();

        return (answer, citations);
    }

    private record ChunkResult(
        Guid   ChunkId,
        Guid   DocumentId,
        int    ChunkIndex,
        string Content,
        string Filename);
}
