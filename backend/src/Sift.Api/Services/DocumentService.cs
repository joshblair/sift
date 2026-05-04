using Amazon.S3;
using Amazon.S3.Model;
using Npgsql;
using Sift.Api.Infrastructure;
using Sift.Api.Models;

namespace Sift.Api.Services;

public class DocumentService(DbConnectionFactory db) : IDocumentService
{
    private static readonly string BucketName =
        Environment.GetEnvironmentVariable("UPLOADS_BUCKET")
        ?? throw new InvalidOperationException("Missing UPLOADS_BUCKET");

    public async Task<List<Document>> ListAsync(Guid tenantId)
    {
        await using var conn = await db.CreateAsync();
        await TenantContext.SetAsync(conn, tenantId);

        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT id, tenant_id, uploaded_by, filename, s3_key, file_type,
                   status, summary, topics, page_count, chunk_count,
                   error_message, created_at, processed_at
            FROM documents
            ORDER BY created_at DESC
            """;

        var docs = new List<Document>();
        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
            docs.Add(MapDocument(reader));

        return docs;
    }

    public async Task<Document?> GetAsync(Guid tenantId, Guid documentId)
    {
        await using var conn = await db.CreateAsync();
        await TenantContext.SetAsync(conn, tenantId);

        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT id, tenant_id, uploaded_by, filename, s3_key, file_type,
                   status, summary, topics, page_count, chunk_count,
                   error_message, created_at, processed_at
            FROM documents
            WHERE id = $1
            """;
        cmd.Parameters.AddWithValue(documentId);

        await using var reader = await cmd.ExecuteReaderAsync();
        return await reader.ReadAsync() ? MapDocument(reader) : null;
    }

    public async Task<UploadUrlResponse> CreatePresignedUploadUrlAsync(
        Guid tenantId, Guid userId, string filename, string fileType)
    {
        var docId  = Guid.NewGuid();
        var s3Key  = $"{tenantId}/{docId}/{filename}";

        // Create document record first so the pipeline can update it on completion.
        await using var conn = await db.CreateAsync();
        await TenantContext.SetAsync(conn, tenantId);

        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO documents (id, tenant_id, uploaded_by, filename, s3_key, file_type, status)
            VALUES ($1, $2, $3, $4, $5, $6, 'pending')
            """;
        cmd.Parameters.AddWithValue(docId);
        cmd.Parameters.AddWithValue(tenantId);
        cmd.Parameters.AddWithValue(userId);
        cmd.Parameters.AddWithValue(filename);
        cmd.Parameters.AddWithValue(s3Key);
        cmd.Parameters.AddWithValue(fileType);
        await cmd.ExecuteNonQueryAsync();

        // Generate presigned PUT URL (15-minute expiry).
        using var s3 = new AmazonS3Client();
        var urlRequest = new GetPreSignedUrlRequest
        {
            BucketName  = BucketName,
            Key         = s3Key,
            Verb        = HttpVerb.PUT,
            Expires     = DateTime.UtcNow.AddMinutes(15),
            ContentType = ContentTypeFor(fileType)
        };
        var uploadUrl = s3.GetPreSignedURL(urlRequest);

        return new UploadUrlResponse(docId, uploadUrl, s3Key);
    }

    public async Task DeleteAsync(Guid tenantId, Guid documentId)
    {
        await using var conn = await db.CreateAsync();
        await TenantContext.SetAsync(conn, tenantId);

        // Fetch s3_key before deleting (RLS ensures tenant can only see their own docs).
        await using var selectCmd = conn.CreateCommand();
        selectCmd.CommandText = "SELECT s3_key FROM documents WHERE id = $1";
        selectCmd.Parameters.AddWithValue(documentId);
        var s3Key = (string?)await selectCmd.ExecuteScalarAsync();

        if (s3Key is null) return;

        await using var deleteCmd = conn.CreateCommand();
        deleteCmd.CommandText = "DELETE FROM documents WHERE id = $1";
        deleteCmd.Parameters.AddWithValue(documentId);
        await deleteCmd.ExecuteNonQueryAsync();

        // Best-effort S3 deletion — don't fail the request if this errors.
        try
        {
            using var s3 = new AmazonS3Client();
            await s3.DeleteObjectAsync(BucketName, s3Key);
        }
        catch { /* log in production */ }
    }

    private static Document MapDocument(NpgsqlDataReader r) => new()
    {
        Id           = r.GetGuid(0),
        TenantId     = r.GetGuid(1),
        UploadedBy   = r.GetGuid(2),
        Filename     = r.GetString(3),
        S3Key        = r.GetString(4),
        FileType     = r.GetString(5),
        Status       = r.GetString(6),
        Summary      = r.IsDBNull(7)  ? null : r.GetString(7),
        Topics       = r.IsDBNull(8)  ? null : (string[])r.GetValue(8),
        PageCount    = r.IsDBNull(9)  ? null : r.GetInt32(9),
        ChunkCount   = r.IsDBNull(10) ? null : r.GetInt32(10),
        ErrorMessage = r.IsDBNull(11) ? null : r.GetString(11),
        CreatedAt    = r.GetDateTime(12),
        ProcessedAt  = r.IsDBNull(13) ? null : r.GetDateTime(13)
    };

    private static string ContentTypeFor(string fileType) => fileType switch
    {
        "pdf"  => "application/pdf",
        "docx" => "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
        "csv"  => "text/csv",
        "txt"  => "text/plain",
        _      => "application/octet-stream"
    };
}
