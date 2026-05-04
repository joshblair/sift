namespace Sift.Api.Models;

public class Tenant
{
    public Guid     Id        { get; set; }
    public string   Name      { get; set; } = "";
    public string   Slug      { get; set; } = "";
    public DateTime CreatedAt { get; set; }
}
