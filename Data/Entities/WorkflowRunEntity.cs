namespace CICD_PipelineOrchestrator.Data.Entities;

public sealed class WorkflowRunEntity
{
    public int Id { get; set; }
    public string SourceConfigurationId { get; set; } = string.Empty;
    public string RepositoryId { get; set; } = string.Empty;
    public string RepositoryName { get; set; } = string.Empty;
    public string ProjectOwner { get; set; } = string.Empty;
    public int Provider { get; set; }
    public string Branch { get; set; } = "main";
    public string Status { get; set; } = string.Empty;
    public long GitLabPipelineId { get; set; }
    public string GitLabWebUrl { get; set; } = string.Empty;
    public string GitLabJobIdsJson { get; set; } = string.Empty;
    public string LogOutput { get; set; } = string.Empty;
    public DateTime StartedUtc { get; set; }
    public DateTime UpdatedUtc { get; set; }
    public DateTime? CompletedUtc { get; set; }
}
