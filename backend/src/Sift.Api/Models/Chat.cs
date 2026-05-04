namespace Sift.Api.Models;

public record ChatRequest(string Question);

public class ChatResponse
{
    public string       Answer   { get; set; } = "";
    public List<Citation> Citations { get; set; } = [];
}

public class Citation
{
    public Guid   DocumentId { get; set; }
    public string Filename   { get; set; } = "";
    public string Excerpt    { get; set; } = "";
    public int    ChunkIndex { get; set; }
}
