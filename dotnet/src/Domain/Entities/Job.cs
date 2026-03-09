namespace TalentMatch.Domain.Entities;

public class Job
{
    public string Id { get; set; } = Guid.NewGuid().ToString();
    public string JobCode { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public string Department { get; set; } = string.Empty;
    public string Organisation { get; set; } = string.Empty;
    public DateTime PostingDate { get; set; }
    public string Status { get; set; } = "active"; // active, closed, draft
    public string? JobDescription { get; set; }
    public string? CurrentConfigVersionId { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
    
    // Navigation properties
    public ICollection<JobConfigVersion> ConfigVersions { get; set; } = new List<JobConfigVersion>();
    public ICollection<Application> Applications { get; set; } = new List<Application>();
}
