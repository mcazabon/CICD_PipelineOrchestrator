namespace CICD_PipelineOrchestrator.Data.Entities;

public sealed class WorkflowDeploymentEntity
{
    public int Id { get; set; }
    public string SourceConfigurationId { get; set; } = string.Empty;
    public string RepositoryId { get; set; } = string.Empty;
    public string RepositoryName { get; set; } = string.Empty;
    public string ProjectOwner { get; set; } = string.Empty;
    public int Provider { get; set; }
    public string Branch { get; set; } = "main";
    public string WorkflowPath { get; set; } = string.Empty;
    public string TemplateId { get; set; } = string.Empty;
    public string YamlContent { get; set; } = string.Empty;
    public DateTime DeployedUtc { get; set; }
}
