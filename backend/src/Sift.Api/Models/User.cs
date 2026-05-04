namespace Sift.Api.Models;

public class User
{
    public Guid     Id          { get; set; }
    public Guid     TenantId    { get; set; }
    public string   CognitoSub  { get; set; } = "";
    public string   Email       { get; set; } = "";
    public string   Role        { get; set; } = "member";
    public DateTime CreatedAt   { get; set; }
}

public record UpdateRoleRequest(string Role);
