using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using CICD_PipelineOrchestrator.Models;

namespace CICD_PipelineOrchestrator.Services;

public sealed class GitLabRepositoryService
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IConfiguration _configuration;
    private string? _overrideBaseUrl;
    private string? _overrideToken;

    public GitLabRepositoryService(IHttpClientFactory httpClientFactory, IConfiguration configuration)
    {
        _httpClientFactory = httpClientFactory;
        _configuration = configuration;
    }

    public void Configure(string? baseUrl, string? token)
    {
        _overrideBaseUrl = string.IsNullOrWhiteSpace(baseUrl) ? null : baseUrl.TrimEnd('/');
        _overrideToken = string.IsNullOrWhiteSpace(token) ? null : token.Trim();
    }

    public async Task<IReadOnlyList<RepositoryDefinition>> GetProjectsAsync(CancellationToken cancellationToken = default)
    {
        var baseUrl = _overrideBaseUrl ?? _configuration["GitLab:BaseUrl"];
        var token = _overrideToken ?? _configuration["GitLab:Token"];
        if (string.IsNullOrWhiteSpace(baseUrl) || string.IsNullOrWhiteSpace(token))
        {
            return Array.Empty<RepositoryDefinition>();
        }

        var client = _httpClientFactory.CreateClient("GitLab");
        client.BaseAddress = new Uri(baseUrl.TrimEnd('/') + "/");
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        var response = await client.GetAsync("api/v4/projects?membership=true&order_by=updated_at&sort=desc&per_page=100", cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            return Array.Empty<RepositoryDefinition>();
        }

        var content = await response.Content.ReadAsStringAsync(cancellationToken);
        var projects = JsonSerializer.Deserialize<List<GitLabProjectResponse>>(content, new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true
        }) ?? [];

        var results = new List<RepositoryDefinition>();
        foreach (var project in projects)
        {
            results.Add(await MapProjectAsync(project, client, cancellationToken));
        }

        return results;
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

        var baseUrl = _overrideBaseUrl ?? _configuration["GitLab:BaseUrl"];
        var token = string.IsNullOrWhiteSpace(accessToken) ? (_overrideToken ?? _configuration["GitLab:Token"]) : accessToken.Trim();
        if (string.IsNullOrWhiteSpace(baseUrl))
        {
            return (Array.Empty<RepositoryDefinition>(), "GitLab base URL is not configured.");
        }

        if (string.IsNullOrWhiteSpace(token))
        {
            return (Array.Empty<RepositoryDefinition>(), "GitLab token is missing.");
        }

        var namespaceName = ParseNamespace(repositoryUrl);
        if (string.IsNullOrWhiteSpace(namespaceName))
        {
            return (Array.Empty<RepositoryDefinition>(), "GitLab namespace could not be parsed from URL.");
        }

        var client = _httpClientFactory.CreateClient("GitLab");
        client.BaseAddress = new Uri(baseUrl.TrimEnd('/') + "/");
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        var endpoints = new[]
        {
            $"api/v4/groups/{Uri.EscapeDataString(namespaceName)}/projects?include_subgroups=true&order_by=updated_at&sort=desc&per_page=100",
            $"api/v4/users/{Uri.EscapeDataString(namespaceName)}/projects?order_by=updated_at&sort=desc&per_page=100"
        };

        var failures = new List<string>();

        foreach (var endpoint in endpoints)
        {
            var response = await client.GetAsync(endpoint, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                failures.Add($"{(int)response.StatusCode} {response.ReasonPhrase} ({endpoint})");
                continue;
            }

            var content = await response.Content.ReadAsStringAsync(cancellationToken);
            var projects = JsonSerializer.Deserialize<List<GitLabProjectResponse>>(content, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            }) ?? [];

            var results = new List<RepositoryDefinition>();
            foreach (var project in projects)
            {
                results.Add(await MapProjectAsync(project, client, cancellationToken));
            }

            return (results, null);
        }

        var error = failures.Count > 0
            ? $"GitLab API request failed: {string.Join("; ", failures)}"
            : "GitLab API did not return repositories for the namespace.";

        return (Array.Empty<RepositoryDefinition>(), error);
    }

    public async Task<RepositoryDefinition?> GetProjectAsync(string repositoryId, CancellationToken cancellationToken = default)
    {
        var projects = await GetProjectsAsync(cancellationToken);
        return projects.FirstOrDefault(p => p.Id == repositoryId || p.Name.Equals(repositoryId, StringComparison.OrdinalIgnoreCase));
    }

    public async Task<(bool Success, string Message)> UpsertWorkflowFileAsync(string projectReference, string branch, string workflowPath, string yaml, string accessToken, CancellationToken cancellationToken = default)
    {
        var baseUrl = _overrideBaseUrl ?? _configuration["GitLab:BaseUrl"];
        var token = string.IsNullOrWhiteSpace(accessToken) ? (_overrideToken ?? _configuration["GitLab:Token"]) : accessToken.Trim();
        if (string.IsNullOrWhiteSpace(baseUrl))
        {
            return (false, "GitLab base URL is not configured.");
        }

        if (string.IsNullOrWhiteSpace(token))
        {
            return (false, "GitLab token is missing.");
        }

        if (string.IsNullOrWhiteSpace(projectReference))
        {
            return (false, "GitLab project reference is missing.");
        }

        if (string.IsNullOrWhiteSpace(workflowPath))
        {
            return (false, "Workflow file path is missing.");
        }

        var targetBranch = string.IsNullOrWhiteSpace(branch) ? "main" : branch.Trim();
        var client = CreateConfiguredClient(baseUrl, token);

        var encodedProject = Uri.EscapeDataString(projectReference);
        var encodedFilePath = Uri.EscapeDataString(workflowPath);
        var fileEndpoint = $"api/v4/projects/{encodedProject}/repository/files/{encodedFilePath}";

        var existsResponse = await client.GetAsync($"{fileEndpoint}?ref={Uri.EscapeDataString(targetBranch)}", cancellationToken);
        var exists = existsResponse.IsSuccessStatusCode;

        var payload = JsonSerializer.Serialize(new
        {
            branch = targetBranch,
            content = yaml,
            commit_message = $"chore(cicd): update {workflowPath}"
        });

        using var requestContent = new StringContent(payload, Encoding.UTF8, "application/json");
        var response = exists
            ? await client.PutAsync(fileEndpoint, requestContent, cancellationToken)
            : await client.PostAsync(fileEndpoint, requestContent, cancellationToken);

        if (response.IsSuccessStatusCode)
        {
            return (true, $"Workflow file '{workflowPath}' committed to branch '{targetBranch}'.");
        }

        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        var preview = string.IsNullOrWhiteSpace(body)
            ? "no response body"
            : body.Length > 260 ? body[..260] + "..." : body;

        return (false, $"GitLab commit failed: {(int)response.StatusCode} {response.ReasonPhrase}. {preview}");
    }

    public async Task<GitLabPipelineTriggerResult> StartPipelineAsync(string projectReference, string branch, string accessToken, CancellationToken cancellationToken = default)
    {
        var client = CreateClientOrThrow(projectReference, accessToken, out var encodedProject, out var validationError);
        if (client is null)
        {
            return new GitLabPipelineTriggerResult
            {
                Success = false,
                Message = validationError
            };
        }

        var targetBranch = string.IsNullOrWhiteSpace(branch) ? "main" : branch.Trim();
        using var requestContent = new FormUrlEncodedContent(
        [
            new KeyValuePair<string, string>("ref", targetBranch)
        ]);

        var response = await client.PostAsync($"api/v4/projects/{encodedProject}/pipeline", requestContent, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            return new GitLabPipelineTriggerResult
            {
                Success = false,
                Message = await BuildFailureMessageAsync("GitLab pipeline trigger failed", response, cancellationToken)
            };
        }

        var payload = await response.Content.ReadAsStringAsync(cancellationToken);
        var pipeline = JsonSerializer.Deserialize<GitLabPipelineResponse>(payload, new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true
        });

        if (pipeline is null)
        {
            return new GitLabPipelineTriggerResult
            {
                Success = false,
                Message = "GitLab pipeline trigger returned an empty response."
            };
        }

        return new GitLabPipelineTriggerResult
        {
            Success = true,
            Message = $"GitLab pipeline #{pipeline.Id} started on branch '{targetBranch}'.",
            PipelineId = pipeline.Id,
            Status = pipeline.Status ?? "created",
            WebUrl = pipeline.WebUrl ?? string.Empty
        };
    }

    public async Task<GitLabPipelineSnapshot> GetPipelineSnapshotAsync(string projectReference, long pipelineId, string accessToken, CancellationToken cancellationToken = default)
    {
        var client = CreateClientOrThrow(projectReference, accessToken, out var encodedProject, out var validationError);
        if (client is null)
        {
            return new GitLabPipelineSnapshot
            {
                Success = false,
                Message = validationError,
                PipelineId = pipelineId
            };
        }

        var pipelineResponse = await client.GetAsync($"api/v4/projects/{encodedProject}/pipelines/{pipelineId}", cancellationToken);
        if (!pipelineResponse.IsSuccessStatusCode)
        {
            return new GitLabPipelineSnapshot
            {
                Success = false,
                Message = await BuildFailureMessageAsync("GitLab pipeline status failed", pipelineResponse, cancellationToken),
                PipelineId = pipelineId
            };
        }

        var pipelinePayload = await pipelineResponse.Content.ReadAsStringAsync(cancellationToken);
        var pipeline = JsonSerializer.Deserialize<GitLabPipelineResponse>(pipelinePayload, new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true
        });

        if (pipeline is null)
        {
            return new GitLabPipelineSnapshot
            {
                Success = false,
                Message = "GitLab pipeline status returned an empty response.",
                PipelineId = pipelineId
            };
        }

        var jobsResponse = await client.GetAsync($"api/v4/projects/{encodedProject}/pipelines/{pipelineId}/jobs?per_page=100", cancellationToken);
        if (!jobsResponse.IsSuccessStatusCode)
        {
            return new GitLabPipelineSnapshot
            {
                Success = false,
                Message = await BuildFailureMessageAsync("GitLab pipeline jobs lookup failed", jobsResponse, cancellationToken),
                PipelineId = pipelineId,
                Status = pipeline.Status ?? string.Empty,
                WebUrl = pipeline.WebUrl ?? string.Empty
            };
        }

        var jobsPayload = await jobsResponse.Content.ReadAsStringAsync(cancellationToken);
        var jobs = JsonSerializer.Deserialize<List<GitLabJobResponse>>(jobsPayload, new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true
        }) ?? [];

        var jobSnapshots = jobs
            .Select(job => new GitLabPipelineJobSnapshot
            {
                JobId = job.Id,
                Name = string.IsNullOrWhiteSpace(job.Name) ? $"job-{job.Id}" : job.Name,
                Stage = job.Stage ?? string.Empty,
                Status = job.Status ?? string.Empty,
                WebUrl = job.WebUrl ?? string.Empty
            })
            .OrderBy(job => job.Stage)
            .ThenBy(job => job.Name)
            .ToList();

        var logLines = new List<string>
        {
            $"[{DateTime.Now:HH:mm:ss}] GitLab pipeline #{pipeline.Id} status: {pipeline.Status ?? "unknown"}"
        };

        foreach (var job in jobSnapshots)
        {
            logLines.Add($"[{DateTime.Now:HH:mm:ss}] Job {job.Name} ({job.Stage}) status: {job.Status}");
            var traceResponse = await client.GetAsync($"api/v4/projects/{encodedProject}/jobs/{job.JobId}/trace", cancellationToken);
            if (!traceResponse.IsSuccessStatusCode)
            {
                continue;
            }

            var trace = await traceResponse.Content.ReadAsStringAsync(cancellationToken);
            if (string.IsNullOrWhiteSpace(trace))
            {
                continue;
            }

            var lines = trace.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(line => line.TrimEnd('\r'))
                .TakeLast(8);

            foreach (var line in lines)
            {
                logLines.Add(line);
            }
        }

        if (logLines.Count > 80)
        {
            logLines = [.. logLines.TakeLast(80)];
        }

        return new GitLabPipelineSnapshot
        {
            Success = true,
            Message = $"GitLab pipeline #{pipeline.Id} status loaded.",
            PipelineId = pipeline.Id,
            Status = pipeline.Status ?? string.Empty,
            WebUrl = pipeline.WebUrl ?? string.Empty,
            Jobs = jobSnapshots,
            LogLines = logLines,
            IsTerminal = IsTerminalStatus(pipeline.Status)
        };
    }

    public async Task<(bool Success, string Message)> CancelPipelineAsync(string projectReference, long pipelineId, string accessToken, CancellationToken cancellationToken = default)
    {
        var client = CreateClientOrThrow(projectReference, accessToken, out var encodedProject, out var validationError);
        if (client is null)
        {
            return (false, validationError);
        }

        var response = await client.PostAsync($"api/v4/projects/{encodedProject}/pipelines/{pipelineId}/cancel", content: null, cancellationToken);
        if (response.IsSuccessStatusCode)
        {
            return (true, $"GitLab pipeline #{pipelineId} cancel request accepted.");
        }

        return (false, await BuildFailureMessageAsync("GitLab pipeline cancel failed", response, cancellationToken));
    }

    private async Task<RepositoryDefinition> MapProjectAsync(GitLabProjectResponse project, HttpClient client, CancellationToken cancellationToken)
    {
        var manifestPaths = await GetManifestPathsAsync(client, project.Id, project.DefaultBranch, cancellationToken);
        var detectedType = DetectProjectType(project, manifestPaths);
        var owner = project.NamespaceInfo?.FullPath ?? project.PathWithNamespace.Split('/').FirstOrDefault() ?? "gitlab";
        var detectionSummary = BuildDetectionSummary(project, detectedType, manifestPaths);
        var fingerprint = BuildProjectFingerprint(project, detectedType, manifestPaths);

        return new RepositoryDefinition
        {
            Id = project.Id.ToString(),
            Name = project.Name,
            Provider = RepositoryProvider.GitLab,
            Owner = owner,
            DefaultBranch = string.IsNullOrWhiteSpace(project.DefaultBranch) ? "main" : project.DefaultBranch,
            Path = string.IsNullOrWhiteSpace(project.PathWithNamespace) ? project.Path : project.PathWithNamespace,
            DetectedType = detectedType,
            Confidence = CalculateConfidence(project, detectedType, manifestPaths),
            DetectionSummary = detectionSummary,
            Fingerprint = fingerprint,
            LastUpdated = DateTime.UtcNow.ToString("yyyy-MM-dd"),
            DeploymentTarget = detectedType == DetectedProjectType.ReactWeb ? "DEV-WEB-01" : detectedType == DetectedProjectType.SqlDatabase ? "DEV-DB-01" : "DEV-WEB-01",
            RequirePeerReview = true,
            RequireChangeTicket = true,
            LiveFetchAvailable = true,
            LiveFetchStatus = "Live fetch available"
        };
    }

    private static ProjectFingerprint BuildProjectFingerprint(GitLabProjectResponse project, DetectedProjectType detectedType, IReadOnlyCollection<string>? manifestPaths)
    {
        var pathList = (manifestPaths ?? []).Select(x => x.ToLowerInvariant()).ToList();
        var signals = new List<ProjectFingerprintSignal>();

        AddFingerprintSignal(signals, "Repository naming", "Project name/namespace indicates app type", 10, HasAnyMatch(project.Name, ["worker", "sync", "api", "portal", "web", "sql", "database"]));
        AddFingerprintSignal(signals, ".NET project manifest", ".csproj/.sln/.slnx", 25, pathList.Any(path => path.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase) || path.EndsWith(".slnx", StringComparison.OrdinalIgnoreCase) || path.EndsWith(".sln", StringComparison.OrdinalIgnoreCase)));
        AddFingerprintSignal(signals, "Worker image definition", "Dockerfile.worker or docker-compose.yml", 20, pathList.Any(path => path.Contains("dockerfile", StringComparison.OrdinalIgnoreCase) || path.EndsWith("docker-compose.yml", StringComparison.OrdinalIgnoreCase)));
        AddFingerprintSignal(signals, "SQL/data manifest", "/SQL/ or .sqlproj or database folder", 30, pathList.Any(path => path.EndsWith(".sqlproj", StringComparison.OrdinalIgnoreCase) || path.Contains("/sql/", StringComparison.OrdinalIgnoreCase) || path.Contains("database", StringComparison.OrdinalIgnoreCase)));
        AddFingerprintSignal(signals, "Frontend manifest", "package.json", 20, pathList.Any(path => path.EndsWith("package.json", StringComparison.OrdinalIgnoreCase)));
        AddFingerprintSignal(signals, "Sync pipeline signal", "sync/etl/integration pattern", 15, HasAnyMatch($"{project.Name} {project.Description ?? string.Empty} {project.PathWithNamespace}", ["sync", "etl", "integration", "pipeline"]));

        var matchedSignals = signals.Where(x => x.Matched).ToList();
        var score = matchedSignals.Sum(x => x.Weight);

        return new ProjectFingerprint
        {
            Type = detectedType,
            Score = score,
            Summary = detectedType == DetectedProjectType.DotNetWorker && matchedSignals.Any(x => x.Name == "SQL/data manifest")
                ? "Detected as .NET worker with SQL integration based on worker + database signals."
                : detectedType == DetectedProjectType.AspNetCoreApi
                    ? "Detected as ASP.NET Core API based on .NET service and web project artifacts."
                    : detectedType == DetectedProjectType.ReactWeb
                        ? "Detected as React/web app based on frontend package manifests."
                        : detectedType == DetectedProjectType.SqlDatabase
                            ? "Detected as SQL/database project based on SQL artifacts and database structure."
                            : "Detected from repository metadata and project artifact signals.",
            Signals = matchedSignals
        };
    }

    private static bool HasAnyMatch(string text, IEnumerable<string> terms)
    {
        return terms.Any(term => text.Contains(term, StringComparison.OrdinalIgnoreCase));
    }

    private static void AddFingerprintSignal(List<ProjectFingerprintSignal> signals, string name, string evidence, int weight, bool matched)
    {
        signals.Add(new ProjectFingerprintSignal
        {
            Name = name,
            Evidence = evidence,
            Weight = weight,
            Matched = matched
        });
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
            return Uri.UnescapeDataString(path);
        }
        catch
        {
            return trimmed.Trim('/');
        }
    }

    private async Task<List<string>> GetManifestPathsAsync(HttpClient client, long projectId, string defaultBranch, CancellationToken cancellationToken)
    {
        var branch = string.IsNullOrWhiteSpace(defaultBranch) ? "main" : defaultBranch;
        var response = await client.GetAsync($"api/v4/projects/{projectId}/repository/tree?ref={Uri.EscapeDataString(branch)}&per_page=100&recursive=true", cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            return [];
        }

        var content = await response.Content.ReadAsStringAsync(cancellationToken);
        var entries = JsonSerializer.Deserialize<List<GitLabTreeEntry>>(content, new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true
        }) ?? [];

        return entries
            .Where(x => !string.IsNullOrWhiteSpace(x.Path))
            .Select(x => x.Path)
            .ToList();
    }

    private static DetectedProjectType DetectProjectType(GitLabProjectResponse project, IEnumerable<string>? manifestPaths)
    {
        var text = $"{project.Name} {project.Description ?? string.Empty} {project.PathWithNamespace} {string.Join(' ', project.Topics ?? [])}".ToLowerInvariant();
        var paths = (manifestPaths ?? []).Select(x => x.ToLowerInvariant()).ToList();
        var scores = new Dictionary<DetectedProjectType, int>
        {
            [DetectedProjectType.AspNetCoreApi] = 0,
            [DetectedProjectType.DotNetWorker] = 0,
            [DetectedProjectType.ReactWeb] = 0,
            [DetectedProjectType.AngularApp] = 0,
            [DetectedProjectType.JavaMaven] = 0,
            [DetectedProjectType.PythonService] = 0,
            [DetectedProjectType.SqlDatabase] = 0,
            [DetectedProjectType.Unknown] = 0
        };

        AddSignal(scores, DetectedProjectType.DotNetWorker, text, ["worker", "job", "background", "consumer", "scheduler", "daemon", "listener"]);
        AddSignal(scores, DetectedProjectType.DotNetWorker, text, ["sync", "syncservice", "synchronization", "etl", "integration", "pipeline"]);
        AddSignal(scores, DetectedProjectType.SqlDatabase, text, ["sql", "database", "dacpac", "mssql", "db", "schema", "migration", "warehouse"]);
        AddSignal(scores, DetectedProjectType.AspNetCoreApi, text, ["api", "webapi", "gateway", "controller", "service"]);
        AddSignal(scores, DetectedProjectType.ReactWeb, text, ["react", "frontend", "portal", "ui", "vite", "angular", "web"]);
        AddSignal(scores, DetectedProjectType.PythonService, text, ["python", "flask", "fastapi", "django"]);
        AddSignal(scores, DetectedProjectType.JavaMaven, text, ["java", "maven", "spring"]);

        AddManifestSignals(scores, paths);

        var hasDotNetProject = paths.Any(path => path.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase) || path.EndsWith(".slnx", StringComparison.OrdinalIgnoreCase) || path.EndsWith(".sln", StringComparison.OrdinalIgnoreCase));
        var hasDockerWorker = paths.Any(path => path.Contains("dockerfile", StringComparison.OrdinalIgnoreCase) || path.EndsWith("docker-compose.yml", StringComparison.OrdinalIgnoreCase));
        var hasSqlManifest = paths.Any(path => path.EndsWith(".sqlproj", StringComparison.OrdinalIgnoreCase) || path.Contains("/sql/", StringComparison.OrdinalIgnoreCase) || path.Contains("sql", StringComparison.OrdinalIgnoreCase) && path.EndsWith(".sql", StringComparison.OrdinalIgnoreCase) || path.Contains("database", StringComparison.OrdinalIgnoreCase));

        if (hasDotNetProject && hasDockerWorker && hasSqlManifest)
        {
            return DetectedProjectType.DotNetWorker;
        }

        if (scores[DetectedProjectType.DotNetWorker] >= 4 && scores[DetectedProjectType.SqlDatabase] >= 2)
        {
            return DetectedProjectType.DotNetWorker;
        }

        if (scores[DetectedProjectType.SqlDatabase] >= 3 && scores[DetectedProjectType.DotNetWorker] == 0)
        {
            return DetectedProjectType.SqlDatabase;
        }

        if (scores[DetectedProjectType.AspNetCoreApi] >= 3)
        {
            return DetectedProjectType.AspNetCoreApi;
        }

        if (scores[DetectedProjectType.ReactWeb] >= 3)
        {
            return DetectedProjectType.ReactWeb;
        }

        if (scores[DetectedProjectType.DotNetWorker] >= 2)
        {
            return DetectedProjectType.DotNetWorker;
        }

        if (scores[DetectedProjectType.PythonService] >= 2)
        {
            return DetectedProjectType.PythonService;
        }

        if (scores[DetectedProjectType.JavaMaven] >= 2)
        {
            return DetectedProjectType.JavaMaven;
        }

        return DetectedProjectType.Unknown;
    }

    private static void AddManifestSignals(IDictionary<DetectedProjectType, int> scores, IReadOnlyCollection<string> paths)
    {
        if (paths.Count == 0)
        {
            return;
        }

        var hasDotNetProject = paths.Any(path => path.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase) || path.EndsWith(".slnx", StringComparison.OrdinalIgnoreCase) || path.EndsWith(".sln", StringComparison.OrdinalIgnoreCase));
        var hasDockerWorker = paths.Any(path => path.Contains("dockerfile", StringComparison.OrdinalIgnoreCase) || path.EndsWith("docker-compose.yml", StringComparison.OrdinalIgnoreCase));
        var hasSqlManifest = paths.Any(path => path.EndsWith(".sqlproj", StringComparison.OrdinalIgnoreCase) || path.Contains("/sql/", StringComparison.OrdinalIgnoreCase) || path.Contains("database", StringComparison.OrdinalIgnoreCase));

        if (hasDotNetProject)
        {
            scores[DetectedProjectType.AspNetCoreApi] += 2;
            scores[DetectedProjectType.DotNetWorker] += 1;
        }

        if (hasDockerWorker)
        {
            scores[DetectedProjectType.DotNetWorker] += 4;
        }

        if (hasSqlManifest)
        {
            scores[DetectedProjectType.SqlDatabase] += 4;
        }

        if (hasDotNetProject && hasDockerWorker && hasSqlManifest)
        {
            scores[DetectedProjectType.DotNetWorker] += 6;
        }

        if (paths.Any(path => path.EndsWith("package.json", StringComparison.OrdinalIgnoreCase) || path.Contains("src/", StringComparison.OrdinalIgnoreCase) && path.Contains("frontend", StringComparison.OrdinalIgnoreCase)))
        {
            scores[DetectedProjectType.ReactWeb] += 2;
        }
    }

    private static void AddSignal(IDictionary<DetectedProjectType, int> scores, DetectedProjectType type, string text, IEnumerable<string> signals)
    {
        foreach (var signal in signals)
        {
            if (text.Contains(signal, StringComparison.OrdinalIgnoreCase))
            {
                scores[type] += 1;
            }
        }
    }

    private static int CalculateConfidence(GitLabProjectResponse project, DetectedProjectType type, IReadOnlyCollection<string>? manifestPaths)
    {
        var text = $"{project.Name} {project.Description ?? string.Empty} {project.PathWithNamespace}".ToLowerInvariant();
        var confidence = 55;
        var pathList = manifestPaths ?? [];

        if (!string.IsNullOrWhiteSpace(project.Description)) confidence += 10;
        if (!string.IsNullOrWhiteSpace(project.DefaultBranch)) confidence += 5;
        if (project.Topics is { Length: > 0 }) confidence += 5;

        if (pathList.Count > 0)
        {
            confidence += 10;
        }

        if (type == DetectedProjectType.AspNetCoreApi) confidence += 15;
        if (type == DetectedProjectType.ReactWeb) confidence += 15;
        if (type == DetectedProjectType.SqlDatabase) confidence += 15;
        if (type == DetectedProjectType.DotNetWorker) confidence += 20;

        if (pathList.Any(path => path.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase)) || pathList.Any(path => path.EndsWith(".sqlproj", StringComparison.OrdinalIgnoreCase)) || pathList.Any(path => path.EndsWith("package.json", StringComparison.OrdinalIgnoreCase)))
        {
            confidence += 10;
        }

        var hasDotNetProject = pathList.Any(path => path.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase) || path.EndsWith(".slnx", StringComparison.OrdinalIgnoreCase) || path.EndsWith(".sln", StringComparison.OrdinalIgnoreCase));
        var hasDockerWorker = pathList.Any(path => path.Contains("dockerfile", StringComparison.OrdinalIgnoreCase) || path.EndsWith("docker-compose.yml", StringComparison.OrdinalIgnoreCase));
        var hasSqlManifest = pathList.Any(path => path.EndsWith(".sqlproj", StringComparison.OrdinalIgnoreCase) || path.Contains("/sql/", StringComparison.OrdinalIgnoreCase) || path.Contains("database", StringComparison.OrdinalIgnoreCase));

        if (hasDotNetProject && hasDockerWorker && hasSqlManifest)
        {
            confidence += 20;
        }

        if (text.Contains("sync", StringComparison.OrdinalIgnoreCase) && (text.Contains("sql", StringComparison.OrdinalIgnoreCase) || text.Contains("database", StringComparison.OrdinalIgnoreCase) || text.Contains("worker", StringComparison.OrdinalIgnoreCase)))
        {
            confidence += 15;
        }

        if (type == DetectedProjectType.DotNetWorker && (text.Contains("sql", StringComparison.OrdinalIgnoreCase) || text.Contains("database", StringComparison.OrdinalIgnoreCase)))
        {
            confidence += 10;
        }

        return Math.Clamp(confidence, 0, 99);
    }

    private static string BuildDetectionSummary(GitLabProjectResponse project, DetectedProjectType type, IReadOnlyCollection<string>? manifestPaths)
    {
        var projectName = string.IsNullOrWhiteSpace(project.Name) ? "GitLab project" : project.Name;
        var text = $"{project.Name} {project.Description ?? string.Empty} {project.PathWithNamespace}".ToLowerInvariant();
        var manifestSummary = manifestPaths is { Count: > 0 }
            ? $" Manifest evidence found: {string.Join(", ", manifestPaths.Take(6))}."
            : " No repository manifest evidence was available from GitLab tree metadata.";

        var detail = type switch
        {
            DetectedProjectType.AspNetCoreApi => "GitLab project metadata suggests an ASP.NET Core API or service-based backend application.",
            DetectedProjectType.ReactWeb => "GitLab project metadata suggests a frontend or web application build.",
            DetectedProjectType.DotNetWorker when text.Contains("sync", StringComparison.OrdinalIgnoreCase) && (text.Contains("sql", StringComparison.OrdinalIgnoreCase) || text.Contains("database", StringComparison.OrdinalIgnoreCase))
                => "GitLab repository tree and metadata indicate a .NET worker service that performs data synchronization and integrates with SQL/database workloads.",
            DetectedProjectType.DotNetWorker => "GitLab repository tree and metadata suggest a .NET background worker or service component.",
            DetectedProjectType.SqlDatabase => "GitLab repository tree and metadata suggest a SQL Server or database project.",
            DetectedProjectType.PythonService => "GitLab project metadata suggests a Python service or application project.",
            DetectedProjectType.JavaMaven => "GitLab project metadata suggests a Java/Maven service.",
            _ => "GitLab project metadata is inconclusive; manual review is recommended before generating a deployment pipeline."
        };

        return $"{detail} Repository: {projectName}.{manifestSummary}";
    }

    private HttpClient? CreateClientOrThrow(string projectReference, string accessToken, out string encodedProject, out string validationError)
    {
        encodedProject = string.Empty;
        validationError = string.Empty;

        var baseUrl = _overrideBaseUrl ?? _configuration["GitLab:BaseUrl"];
        var token = string.IsNullOrWhiteSpace(accessToken) ? (_overrideToken ?? _configuration["GitLab:Token"]) : accessToken.Trim();
        if (string.IsNullOrWhiteSpace(baseUrl))
        {
            validationError = "GitLab base URL is not configured.";
            return null;
        }

        if (string.IsNullOrWhiteSpace(token))
        {
            validationError = "GitLab token is missing.";
            return null;
        }

        if (string.IsNullOrWhiteSpace(projectReference))
        {
            validationError = "GitLab project reference is missing.";
            return null;
        }

        encodedProject = Uri.EscapeDataString(projectReference);
        return CreateConfiguredClient(baseUrl, token);
    }

    private HttpClient CreateConfiguredClient(string baseUrl, string token)
    {
        var client = _httpClientFactory.CreateClient("GitLab");
        client.BaseAddress = new Uri(baseUrl.TrimEnd('/') + "/");
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client;
    }

    private static bool IsTerminalStatus(string? status)
    {
        return status?.ToLowerInvariant() switch
        {
            "success" => true,
            "failed" => true,
            "canceled" => true,
            "skipped" => true,
            "manual" => true,
            _ => false
        };
    }

    private static async Task<string> BuildFailureMessageAsync(string prefix, HttpResponseMessage response, CancellationToken cancellationToken)
    {
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        var preview = string.IsNullOrWhiteSpace(body)
            ? "no response body"
            : body.Length > 260 ? body[..260] + "..." : body;
        return $"{prefix}: {(int)response.StatusCode} {response.ReasonPhrase}. {preview}";
    }

    private sealed class GitLabPipelineResponse
    {
        public long Id { get; set; }
        public string? Status { get; set; }

        [JsonPropertyName("web_url")]
        public string? WebUrl { get; set; }
    }

    private sealed class GitLabJobResponse
    {
        public long Id { get; set; }
        public string? Name { get; set; }
        public string? Stage { get; set; }
        public string? Status { get; set; }

        [JsonPropertyName("web_url")]
        public string? WebUrl { get; set; }
    }

    private sealed class GitLabTreeEntry
    {
        public string Path { get; set; } = string.Empty;
        public string Type { get; set; } = string.Empty;
    }
}
