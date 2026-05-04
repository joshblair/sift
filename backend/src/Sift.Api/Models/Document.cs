namespace Sift.Api.Models;

public class Document
{
    public Guid   Id          { get; set; }
    public Guid   TenantId    { get; set; }
    public Guid   UploadedBy  { get; set; }
    public string Filename    { get; set; } = "";
    public string S3Key       { get; set; } = "";
    public string FileType    { get; set; } = "";
    public string Status      { get; set; } = "pending";
    public string? Summary    { get; set; }
    public string[]? Topics   { get; set; }
    public int?   PageCount   { get; set; }
    public int?   ChunkCount  { get; set; }
    public string? ErrorMessage { get; set; }
    public DateTime CreatedAt   { get; set; }
    public DateTime? ProcessedAt { get; set; }
}

public record UploadUrlRequest(string Filename, string FileType);

public record UploadUrlResponse(Guid DocumentId, string UploadUrl, string S3Key);
