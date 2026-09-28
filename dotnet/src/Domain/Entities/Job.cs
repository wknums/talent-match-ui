using System.Text.Json.Serialization;

namespace TalentMatch.Domain.Entities;

public class Job
{
    public string Id { get; set; } = Guid.NewGuid().ToString();
    public string JobCode { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public string Department { get; set; } = string.Empty;
    public string Organisation { get; set; } = string.Empty;
    public string? OrganizationId { get; set; }
    public string? DepartmentId { get; set; }
    public DateTime PostingDate { get; set; }
    public string Status { get; set; } = "active"; // active, closed, draft
    public string? JobDescription { get; set; }
    public string? CurrentConfigVersionId { get; set; }
    public string? CreatedBy { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
    
    // Navigation properties
    [JsonIgnore]
    public ICollection<JobConfigVersion> ConfigVersions { get; set; } = new List<JobConfigVersion>();
    [JsonIgnore]
    public ICollection<Application> Applications { get; set; } = new List<Application>();
    [JsonIgnore]
    public Organization? Organization { get; set; }
    [JsonIgnore]
    public Department? DepartmentEntity { get; set; }
}
