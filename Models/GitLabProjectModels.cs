using System.Text.Json.Serialization;

namespace CICD_PipelineOrchestrator.Models;

public sealed class GitLabProjectResponse
{
    public long Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string Path { get; set; } = string.Empty;

    [JsonPropertyName("path_with_namespace")]
    public string PathWithNamespace { get; set; } = string.Empty;

    public string? Description { get; set; }

    [JsonPropertyName("default_branch")]
    public string DefaultBranch { get; set; } = "main";

    [JsonPropertyName("web_url")]
    public string? WebUrl { get; set; }

    public string? Visibility { get; set; }
    public string?[] Topics { get; set; } = Array.Empty<string>();

    [JsonPropertyName("namespace")]
    public GitLabNamespaceInfo? NamespaceInfo { get; set; }
}

public sealed class GitLabNamespaceInfo
{
    [JsonPropertyName("full_path")]
    public string FullPath { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;
}
