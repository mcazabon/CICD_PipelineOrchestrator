namespace CICD_PipelineOrchestrator.Data.Entities;

public sealed class RepositoryProjectEntity
{
    public int Id { get; set; }
    public string SourceConfigurationId { get; set; } = string.Empty;
    public string RepositoryId { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public int Provider { get; set; }
    public string Owner { get; set; } = string.Empty;
    public string DefaultBranch { get; set; } = "main";
    public string Path { get; set; } = string.Empty;
    public int DetectedType { get; set; }
    public int Confidence { get; set; }
    public string DetectionSummary { get; set; } = string.Empty;
    public string LastUpdated { get; set; } = string.Empty;
    public string DeploymentTarget { get; set; } = "DEV-WEB-01";
    public bool RequirePeerReview { get; set; }
    public bool RequireChangeTicket { get; set; }
    public bool LiveFetchAvailable { get; set; }
    public string LiveFetchStatus { get; set; } = string.Empty;
    public string SourceDisplayName { get; set; } = string.Empty;
    public string SourceRepositoryUrl { get; set; } = string.Empty;
    public string FingerprintJson { get; set; } = string.Empty;
}
