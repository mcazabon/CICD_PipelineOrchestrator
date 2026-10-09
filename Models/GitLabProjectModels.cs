namespace CICD_PipelineOrchestrator.Models;

public sealed class GitLabProjectResponse
{
    public long Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string Path { get; set; } = string.Empty;
    public string PathWithNamespace { get; set; } = string.Empty;
    public string Namespace { get; set; } = string.Empty;
    public string? Description { get; set; }
    public string DefaultBranch { get; set; } = "main";
    public string? WebUrl { get; set; }
    public string? Visibility { get; set; }
    public string?[] Topics { get; set; } = Array.Empty<string>();
    public GitLabNamespaceInfo? NamespaceInfo { get; set; }
}

public sealed class GitLabNamespaceInfo
{
    public string FullPath { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
}
