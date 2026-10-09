using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using CICD_PipelineOrchestrator.Models;

namespace CICD_PipelineOrchestrator.Services;

public sealed class GitHubRepositoryService
{
    private readonly IHttpClientFactory _httpClientFactory;

    public GitHubRepositoryService(IHttpClientFactory httpClientFactory)
    {
        _httpClientFactory = httpClientFactory;
    }

    public async Task<IReadOnlyList<RepositoryDefinition>> GetProjectsForNamespaceAsync(string repositoryUrl, string accessToken, CancellationToken cancellationToken = default)
    {
        var (repositories, _) = await TryGetProjectsForNamespaceAsync(repositoryUrl, accessToken, cancellationToken);
        return repositories;
    }

    public async Task<(IReadOnlyList<RepositoryDefinition> Repositories, string? Error)> TryGetProjectsForNamespaceAsync(string repositoryUrl, string accessToken, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(repositoryUrl))
        {
            return (Array.Empty<RepositoryDefinition>(), "Repository URL is required.");
        }

        if (string.IsNullOrWhiteSpace(accessToken))
        {
            return (Array.Empty<RepositoryDefinition>(), "GitHub token is missing.");
        }

        var owner = ParseNamespace(repositoryUrl);
        if (string.IsNullOrWhiteSpace(owner))
        {
            return (Array.Empty<RepositoryDefinition>(), "GitHub owner/namespace could not be parsed from URL.");
        }

        var client = _httpClientFactory.CreateClient("GitHub");
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", accessToken.Trim());
        client.DefaultRequestHeaders.UserAgent.ParseAdd("CICD-Pipeline-Orchestrator");

        var endpoints = new[]
        {
            $"users/{Uri.EscapeDataString(owner)}/repos?per_page=100",
            $"orgs/{Uri.EscapeDataString(owner)}/repos?per_page=100"
        };

        var failures = new List<string>();

        foreach (var endpoint in endpoints)
        {
            var response = await client.GetAsync($"https://api.github.com/{endpoint}", cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                failures.Add($"{(int)response.StatusCode} {response.ReasonPhrase} ({endpoint})");
                continue;
            }

            var content = await response.Content.ReadAsStringAsync(cancellationToken);
            var repos = JsonSerializer.Deserialize<List<GitHubRepositoryResponse>>(content, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            }) ?? [];

            var result = repos
                .Where(r => !string.IsNullOrWhiteSpace(r.Name))
                .Select(r => new RepositoryDefinition
                {
                    Id = $"github-{r.Id}",
                    Name = r.Name,
                    Provider = RepositoryProvider.GitHub,
                    Owner = owner,
                    DefaultBranch = string.IsNullOrWhiteSpace(r.DefaultBranch) ? "main" : r.DefaultBranch,
                    Path = r.HtmlUrl ?? $"https://github.com/{owner}/{r.Name}",
                    DetectedType = DetectProjectType(r),
                    Confidence = CalculateConfidence(r),
                    DetectionSummary = BuildDetectionSummary(r),
                    LastUpdated = DateTime.UtcNow.ToString("yyyy-MM-dd"),
                    DeploymentTarget = "DEV-WEB-01",
                    RequirePeerReview = true,
                    RequireChangeTicket = true,
                    LiveFetchAvailable = true,
                    LiveFetchStatus = "Live fetch available",
                    Fingerprint = new ProjectFingerprint
                    {
                        Type = DetectProjectType(r),
                        Score = CalculateConfidence(r),
                        Summary = BuildDetectionSummary(r),
                        Signals = []
                    }
                })
                .ToList();

            return (result, null);
        }

        var error = failures.Count > 0
            ? $"GitHub API request failed: {string.Join("; ", failures)}"
            : "GitHub API did not return repositories for the namespace.";

        return (Array.Empty<RepositoryDefinition>(), error);
    }

    public async Task<(bool Success, string Message)> UpsertWorkflowFileAsync(string repositoryPath, string branch, string workflowPath, string yaml, string accessToken, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(accessToken))
        {
            return (false, "GitHub token is missing.");
        }

        if (!TryParseRepositoryPath(repositoryPath, out var owner, out var repo))
        {
            return (false, "GitHub repository path could not be parsed.");
        }

        var targetBranch = string.IsNullOrWhiteSpace(branch) ? "main" : branch.Trim();
        var client = _httpClientFactory.CreateClient("GitHub");
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", accessToken.Trim());
        client.DefaultRequestHeaders.UserAgent.ParseAdd("CICD-Pipeline-Orchestrator");
        client.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");

        var endpoint = $"https://api.github.com/repos/{owner}/{repo}/contents/{workflowPath}";
        string? sha = null;

        var getResponse = await client.GetAsync($"{endpoint}?ref={Uri.EscapeDataString(targetBranch)}", cancellationToken);
        if (getResponse.IsSuccessStatusCode)
        {
            var existingContent = await getResponse.Content.ReadAsStringAsync(cancellationToken);
            var existingJson = JsonDocument.Parse(existingContent);
            if (existingJson.RootElement.TryGetProperty("sha", out var shaElement))
            {
                sha = shaElement.GetString();
            }
        }

        var payload = new Dictionary<string, object?>
        {
            ["message"] = $"chore(cicd): update {workflowPath}",
            ["content"] = Convert.ToBase64String(Encoding.UTF8.GetBytes(yaml)),
            ["branch"] = targetBranch
        };

        if (!string.IsNullOrWhiteSpace(sha))
        {
            payload["sha"] = sha;
        }

        var body = JsonSerializer.Serialize(payload);
        using var requestContent = new StringContent(body, Encoding.UTF8, "application/json");
        var putResponse = await client.PutAsync(endpoint, requestContent, cancellationToken);
        if (putResponse.IsSuccessStatusCode)
        {
            return (true, $"Workflow file '{workflowPath}' committed to branch '{targetBranch}'.");
        }

        var responseBody = await putResponse.Content.ReadAsStringAsync(cancellationToken);
        var preview = string.IsNullOrWhiteSpace(responseBody)
            ? "no response body"
            : responseBody.Length > 260 ? responseBody[..260] + "..." : responseBody;

        return (false, $"GitHub commit failed: {(int)putResponse.StatusCode} {putResponse.ReasonPhrase}. {preview}");
    }

    private static bool TryParseRepositoryPath(string repositoryPath, out string owner, out string repo)
    {
        owner = string.Empty;
        repo = string.Empty;

        if (string.IsNullOrWhiteSpace(repositoryPath))
        {
            return false;
        }

        var trimmed = repositoryPath.Trim().TrimEnd('/');
        if (trimmed.EndsWith(".git", StringComparison.OrdinalIgnoreCase))
        {
            trimmed = trimmed[..^4];
        }

        if (Uri.TryCreate(trimmed, UriKind.Absolute, out var uri))
        {
            var segments = uri.AbsolutePath.Trim('/').Split('/', StringSplitOptions.RemoveEmptyEntries);
            if (segments.Length >= 2)
            {
                owner = segments[0];
                repo = segments[1];
                return true;
            }

            return false;
        }

        var rawSegments = trimmed.Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (rawSegments.Length >= 2)
        {
            owner = rawSegments[0];
            repo = rawSegments[1];
            return true;
        }

        return false;
    }

    private static string ParseNamespace(string repositoryUrl)
    {
        if (string.IsNullOrWhiteSpace(repositoryUrl))
        {
            return string.Empty;
        }

        var trimmed = repositoryUrl.Trim().TrimEnd('/');
        if (trimmed.EndsWith(".git", StringComparison.OrdinalIgnoreCase))
        {
            trimmed = trimmed[..^4];
        }

        if (!trimmed.Contains("//", StringComparison.Ordinal))
        {
            trimmed = $"https://{trimmed}";
        }

        try
        {
            var uri = new Uri(trimmed);
            var path = uri.AbsolutePath.Trim('/');
            if (string.IsNullOrWhiteSpace(path) || path.Contains("/repos"))
            {
                return string.Empty;
            }

            return Uri.UnescapeDataString(path);
        }
        catch
        {
            return trimmed.Trim('/');
        }
    }

    private static DetectedProjectType DetectProjectType(GitHubRepositoryResponse repo)
    {
        var stacked = $"{repo.Name} {repo.Description ?? string.Empty}".ToLowerInvariant();
        if (stacked.Contains("api") || stacked.Contains("gateway") || stacked.Contains("service"))
        {
            return DetectedProjectType.AspNetCoreApi;
        }

        if (stacked.Contains("worker") || stacked.Contains("job") || stacked.Contains("sync") || stacked.Contains("etl"))
        {
            return DetectedProjectType.DotNetWorker;
        }

        if (stacked.Contains("react") || stacked.Contains("frontend") || stacked.Contains("portal") || stacked.Contains("ui"))
        {
            return DetectedProjectType.ReactWeb;
        }

        if (stacked.Contains("sql") || stacked.Contains("database"))
        {
            return DetectedProjectType.SqlDatabase;
        }

        return DetectedProjectType.Unknown;
    }

    private static int CalculateConfidence(GitHubRepositoryResponse repo)
    {
        var confidence = 55;
        if (!string.IsNullOrWhiteSpace(repo.Description)) confidence += 10;
        if (!string.IsNullOrWhiteSpace(repo.DefaultBranch)) confidence += 5;
        if (!string.IsNullOrWhiteSpace(repo.HtmlUrl)) confidence += 10;

        var type = DetectProjectType(repo);
        if (type == DetectedProjectType.AspNetCoreApi) confidence += 15;
        if (type == DetectedProjectType.DotNetWorker) confidence += 15;
        if (type == DetectedProjectType.ReactWeb) confidence += 15;
        if (type == DetectedProjectType.SqlDatabase) confidence += 15;

        return Math.Clamp(confidence, 0, 99);
    }

    private static string BuildDetectionSummary(GitHubRepositoryResponse repo)
    {
        var type = DetectProjectType(repo);
        var detail = type switch
        {
            DetectedProjectType.AspNetCoreApi => "GitHub repository metadata suggests an API or service-based backend application.",
            DetectedProjectType.DotNetWorker => "GitHub repository metadata suggests a worker or background processing service.",
            DetectedProjectType.ReactWeb => "GitHub repository metadata suggests a web or frontend application.",
            DetectedProjectType.SqlDatabase => "GitHub repository metadata suggests a SQL/database-focused project.",
            _ => "GitHub repository metadata is inconclusive and requires manual confirmation."
        };

        return $"{detail} Repository: {repo.Name}.";
    }

    private sealed class GitHubRepositoryResponse
    {
        public string Name { get; set; } = string.Empty;
        public string? Description { get; set; }
        public string? DefaultBranch { get; set; }
        public string? HtmlUrl { get; set; }
        public long Id { get; set; }
    }
}
