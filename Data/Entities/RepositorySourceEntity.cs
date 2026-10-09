namespace CICD_PipelineOrchestrator.Data.Entities;

public sealed class RepositorySourceEntity
{
    public string SourceConfigurationId { get; set; } = string.Empty;
    public int Provider { get; set; }
    public string RepositoryUrl { get; set; } = string.Empty;
    public string AccessTokenEncrypted { get; set; } = string.Empty;
    public DateTime CreatedUtc { get; set; }
    public DateTime UpdatedUtc { get; set; }
}
