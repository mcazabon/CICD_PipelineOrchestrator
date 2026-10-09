namespace CICD_PipelineOrchestrator.Models;

public enum PipelineStatus
{
    Healthy,
    Running,
    Warning,
    Failed
}

public sealed class PipelineStage
{
    public string Name { get; set; } = string.Empty;
    public string Status { get; set; } = "Pending";
    public int Progress { get; set; }
    public string Duration { get; set; } = "--";
}

public sealed class PipelineDefinition
{
    public int Id { get; set; }
    public string RepositoryId { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Project { get; set; } = string.Empty;
    public string Branch { get; set; } = "main";
    public string Trigger { get; set; } = "Manual";
    public PipelineStatus Status { get; set; } = PipelineStatus.Healthy;
    public DateTime LastRun { get; set; } = DateTime.UtcNow;
    public int SuccessRate { get; set; } = 100;
    public List<PipelineStage> Stages { get; set; } = [];
    public List<string> ConsoleOutput { get; set; } = [];
    public long? ExternalPipelineId { get; set; }
    public string ExternalPipelineUrl { get; set; } = string.Empty;
}

public sealed class PipelineSummary
{
    public int TotalPipelines { get; set; }
    public int ActiveRuns { get; set; }
    public int DeploymentsToday { get; set; }
    public int SuccessRate { get; set; }
}

public sealed class PipelineActivity
{
    public string Title { get; set; } = string.Empty;
    public string Detail { get; set; } = string.Empty;
    public DateTime Timestamp { get; set; } = DateTime.UtcNow;
    public string Tone { get; set; } = "neutral";
}

public sealed class WorkflowDeploymentResult
{
    public bool Success { get; set; }
    public bool TrackingRecorded { get; set; }
    public string Message { get; set; } = string.Empty;
    public string WorkflowPath { get; set; } = string.Empty;
    public string Branch { get; set; } = "main";
    public DateTime? DeployedUtc { get; set; }
}

public enum WorkflowDiagnosticSeverity
{
    Info,
    Warning,
    Error
}

public sealed class WorkflowDiagnosticIssue
{
    public WorkflowDiagnosticSeverity Severity { get; set; } = WorkflowDiagnosticSeverity.Info;
    public string Message { get; set; } = string.Empty;
    public string Detail { get; set; } = string.Empty;
    public string Recommendation { get; set; } = string.Empty;
    public string SuggestedTemplateId { get; set; } = string.Empty;
    public string SuggestedYamlSnippet { get; set; } = string.Empty;
}
