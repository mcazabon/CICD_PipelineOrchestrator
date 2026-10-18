namespace CICD_PipelineOrchestrator.Data.Entities;

public sealed class AgentInvocationEntity
{
    public int Id { get; set; }
    public string SourceConfigurationId { get; set; } = string.Empty;
    public string? ProjectReference { get; set; }
    public string InvokedBy { get; set; } = string.Empty;
    public DateTime InvokedUtc { get; set; }
    public string? WorkflowId { get; set; }
    public bool Success { get; set; }
    public string Message { get; set; } = string.Empty;
    public string AuthScheme { get; set; } = string.Empty;
}
