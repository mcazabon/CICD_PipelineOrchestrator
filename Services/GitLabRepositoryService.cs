using System.Net.Http.Headers;
using System.Text.Json;
using CICD_PipelineOrchestrator.Models;

namespace CICD_PipelineOrchestrator.Services;

public sealed class GitLabRepositoryService
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IConfiguration _configuration;

    public GitLabRepositoryService(IHttpClientFactory httpClientFactory, IConfiguration configuration)
    {
        _httpClientFactory = httpClientFactory;
        _configuration = configuration;
    }

    public async Task<IReadOnlyList<RepositoryDefinition>> GetProjectsAsync(CancellationToken cancellationToken = default)
    {
        var baseUrl = _configuration["GitLab:BaseUrl"];
        var token = _configuration["GitLab:Token"];
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

    public async Task<RepositoryDefinition?> GetProjectAsync(string repositoryId, CancellationToken cancellationToken = default)
    {
        var projects = await GetProjectsAsync(cancellationToken);
        return projects.FirstOrDefault(p => p.Id == repositoryId || p.Name.Equals(repositoryId, StringComparison.OrdinalIgnoreCase));
    }

    private async Task<RepositoryDefinition> MapProjectAsync(GitLabProjectResponse project, HttpClient client, CancellationToken cancellationToken)
    {
        var manifestPaths = await GetManifestPathsAsync(client, project.Id, project.DefaultBranch, cancellationToken);
        var detectedType = DetectProjectType(project, manifestPaths);
        var owner = project.NamespaceInfo?.FullPath ?? project.Namespace ?? project.PathWithNamespace.Split('/').FirstOrDefault() ?? "gitlab";
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
            RequireChangeTicket = true
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

    private sealed class GitLabTreeEntry
    {
        public string Path { get; set; } = string.Empty;
        public string Type { get; set; } = string.Empty;
    }
}
