using System.Text.Json.Serialization;

namespace TalentMatch.Domain.Entities;

public class ApplicationDocument
{
    public string Id { get; set; } = Guid.NewGuid().ToString();
    public string ApplicationId { get; set; } = string.Empty;
    public string FileName { get; set; } = string.Empty;
    public string FileType { get; set; } = string.Empty;
    public long FileSize { get; set; }
    public string Fingerprint { get; set; } = string.Empty;
    public string? ContentBase64 { get; set; }
    public DateTime UploadTimestamp { get; set; } = DateTime.UtcNow;
    
    [JsonIgnore]
    public Application? Application { get; set; }
}
