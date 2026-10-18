namespace CICD_PipelineOrchestrator.Models;

public sealed class GitLabPipelineTriggerResult
{
    public bool Success { get; set; }
    public string Message { get; set; } = string.Empty;
    public long PipelineId { get; set; }
    public string Status { get; set; } = string.Empty;
    public string WebUrl { get; set; } = string.Empty;
}

public sealed class GitLabPipelineSnapshot
{
    public bool Success { get; set; }
    public string Message { get; set; } = string.Empty;
    public long PipelineId { get; set; }
    public string Status { get; set; } = string.Empty;
    public string WebUrl { get; set; } = string.Empty;
    public List<GitLabPipelineJobSnapshot> Jobs { get; set; } = [];
    public List<string> LogLines { get; set; } = [];
    public bool IsTerminal { get; set; }
}

public sealed class GitLabPipelineJobSnapshot
{
    public long JobId { get; set; }
    public string Name { get; set; } = string.Empty;
    public string Stage { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty;
    public string WebUrl { get; set; } = string.Empty;
}

public sealed class RepositoryRunContext
{
    public string SourceConfigurationId { get; set; } = string.Empty;
    public RepositoryDefinition Repository { get; set; } = new();
    public string AccessToken { get; set; } = string.Empty;
    public string ProjectReference { get; set; } = string.Empty;
}

public sealed class GitLabCiLintResult
{
    public bool Success { get; set; }
    public bool Valid { get; set; }
    public string Message { get; set; } = string.Empty;
    public List<string> Errors { get; set; } = [];
    public List<string> Warnings { get; set; } = [];
    public string MergedYaml { get; set; } = string.Empty;
}
