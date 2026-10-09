using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using CICD_PipelineOrchestrator.Data;
using CICD_PipelineOrchestrator.Data.Entities;
using CICD_PipelineOrchestrator.Models;
using Microsoft.EntityFrameworkCore;

namespace CICD_PipelineOrchestrator.Services;

public sealed partial class RepositoryCatalogService
{
    private readonly GitLabRepositoryService? _gitLabRepositoryService;
    private readonly GitHubRepositoryService? _gitHubRepositoryService;
    private readonly IDbContextFactory<RepositoryCatalogDbContext> _dbContextFactory;
    private readonly List<RepositoryDefinition> _customRepositories = [];
    private readonly List<RepositoryConfiguration> _configurations = [];
    private readonly string _storagePath;

    public RepositoryCatalogService(
        IDbContextFactory<RepositoryCatalogDbContext> dbContextFactory,
        GitLabRepositoryService? gitLabRepositoryService = null,
        GitHubRepositoryService? gitHubRepositoryService = null)
    {
        _dbContextFactory = dbContextFactory;
        _gitLabRepositoryService = gitLabRepositoryService;
        _gitHubRepositoryService = gitHubRepositoryService;
        _storagePath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CICD_PipelineOrchestrator", "repository-config.json");
        DeletePersistedStorageIfPresent();
        LoadFromDatabase();
    }

    public void AddRepository(RepositoryProvider provider, string repositoryUrl, string accessToken)
    {
        _ = AddRepositoryWithProgressAsync(provider, repositoryUrl, accessToken, progress: null, CancellationToken.None).GetAwaiter().GetResult();
    }

    public async Task<IReadOnlyList<RepositoryDefinition>> AddRepositoryWithProgressAsync(
        RepositoryProvider provider,
        string repositoryUrl,
        string accessToken,
        IProgress<string>? progress,
        CancellationToken cancellationToken)
    {
        progress?.Report("Validating repository source...");
        await Task.Delay(120, cancellationToken);

        if (string.IsNullOrWhiteSpace(repositoryUrl))
        {
            progress?.Report("Repository URL is required.");
            return GetSavedRepositorySnapshot();
        }

        var normalizedUrl = NormalizeRepositoryUrl(repositoryUrl);
        if (string.IsNullOrWhiteSpace(normalizedUrl))
        {
            progress?.Report("Repository URL is invalid.");
            return GetSavedRepositorySnapshot();
        }

        var sourceConfigurationId = CreateRepositoryId(provider, normalizedUrl);
        if (_configurations.Any(x => x.Provider == provider && x.RepositoryUrl.Equals(normalizedUrl, StringComparison.OrdinalIgnoreCase)))
        {
            progress?.Report("Repository source is already configured.");
            return GetSavedRepositorySnapshot();
        }

        var config = new RepositoryConfiguration
        {
            Provider = provider,
            RepositoryUrl = normalizedUrl,
            AccessToken = accessToken ?? string.Empty
        };

        _configurations.Add(config);
        PersistSourceConfiguration(config, sourceConfigurationId);
        progress?.Report($"Registered {provider} source in runtime catalog.");
        await Task.Delay(120, cancellationToken);

        IReadOnlyList<RepositoryDefinition> results;
        var fetchBlocked = false;
        var fetchStatus = "Live fetch available";

        try
        {
            progress?.Report("Connecting to provider and discovering repositories...");
            var (discoveredRepositories, fetchError) = await FetchRepositoriesForConfigurationWithDiagnosticsAsync(config, cancellationToken);
            results = discoveredRepositories;

            if (!string.IsNullOrWhiteSpace(fetchError))
            {
                fetchBlocked = true;
                fetchStatus = $"Live fetch unavailable: {fetchError}";
                progress?.Report(fetchStatus);
            }
        }
        catch (OperationCanceledException)
        {
            _configurations.RemoveAll(x => x.Provider == provider && x.RepositoryUrl.Equals(normalizedUrl, StringComparison.OrdinalIgnoreCase));
            RemoveSourceConfiguration(sourceConfigurationId);
            progress?.Report("Add repository canceled.");
            throw;
        }
        catch (Exception ex)
        {
            results = [];
            fetchBlocked = true;
            fetchStatus = $"Live fetch unavailable: {ex.Message}";
            progress?.Report(fetchStatus);
        }

        _customRepositories.RemoveAll(r => string.Equals(r.SourceConfigurationId, sourceConfigurationId, StringComparison.OrdinalIgnoreCase));

        if (results.Count > 0)
        {
            foreach (var repository in results)
            {
                repository.Owner ??= ParseRepositoryOwner(config.RepositoryUrl);
                repository.Path ??= config.RepositoryUrl;
                repository.Provider = config.Provider;
                repository.LiveFetchAvailable = true;
                repository.LiveFetchStatus = "Live fetch available";
                repository.SourceConfigurationId = sourceConfigurationId;
                repository.SourceDisplayName = ParseRepositoryName(config.RepositoryUrl);
                repository.SourceRepositoryUrl = config.RepositoryUrl;
                _customRepositories.Add(repository);
            }

            progress?.Report($"Discovered {results.Count} repositories from namespace.");
            PersistRepositoriesForSource(sourceConfigurationId, _customRepositories.Where(r => string.Equals(r.SourceConfigurationId, sourceConfigurationId, StringComparison.OrdinalIgnoreCase)));
            DeletePersistedStorageIfPresent();
            return GetSavedRepositorySnapshot();
        }

        if (!fetchBlocked)
        {
            fetchBlocked = true;
            fetchStatus = "Live fetch unavailable: namespace returned no repositories.";
            progress?.Report(fetchStatus);
        }

        progress?.Report("Added source in blocked/offline mode.");

        _customRepositories.Add(new RepositoryDefinition
        {
            Id = sourceConfigurationId,
            Name = ParseRepositoryName(config.RepositoryUrl),
            Provider = config.Provider,
            Owner = ParseRepositoryOwner(config.RepositoryUrl),
            DefaultBranch = "main",
            Path = config.RepositoryUrl,
            DetectedType = DetectedProjectType.Unknown,
            Confidence = 0,
            DetectionSummary = "Repository added. Click Analyze to inspect the project signature and detection score.",
            DeploymentTarget = "DEV-WEB-01",
            RequirePeerReview = true,
            RequireChangeTicket = true,
            LiveFetchAvailable = !fetchBlocked,
            LiveFetchStatus = fetchStatus,
            SourceConfigurationId = sourceConfigurationId,
            SourceDisplayName = ParseRepositoryName(config.RepositoryUrl),
            SourceRepositoryUrl = config.RepositoryUrl
        });

        PersistRepositoriesForSource(sourceConfigurationId, _customRepositories.Where(r => string.Equals(r.SourceConfigurationId, sourceConfigurationId, StringComparison.OrdinalIgnoreCase)));
        DeletePersistedStorageIfPresent();
        return GetSavedRepositorySnapshot();
    }

    public void RemoveRepository(string repositoryId)
    {
        if (string.IsNullOrWhiteSpace(repositoryId))
        {
            return;
        }

        var sourceConfigurationId = ResolveSourceConfigurationId(repositoryId);
        var match = _configurations.FirstOrDefault(x => CreateRepositoryId(x.Provider, x.RepositoryUrl) == sourceConfigurationId);
        if (match is null)
        {
            return;
        }

        _configurations.Remove(match);

        _customRepositories.RemoveAll(r =>
            string.Equals(r.SourceConfigurationId, sourceConfigurationId, StringComparison.OrdinalIgnoreCase)
            || string.Equals(r.Id, repositoryId, StringComparison.OrdinalIgnoreCase));

        RemoveSourceConfiguration(sourceConfigurationId);
        DeletePersistedStorageIfPresent();
    }

    public string GetLastSavedToken()
    {
        return string.Empty;
    }

    public void ClearSavedRepositories()
    {
        _configurations.Clear();
        _customRepositories.Clear();
        ClearDatabaseState();
        DeletePersistedStorageIfPresent();
    }

    public IReadOnlyList<RepositoryDefinition> GetRepositories(bool refreshLive = true)
    {
        if (refreshLive)
        {
            RefreshConfiguredRepositories();
        }

        if (_customRepositories.Count == 0 && _configurations.Count > 0)
        {
            return BuildSavedRepositorySnapshot();
        }

        return
        [
            .. _customRepositories
                .GroupBy(r => r.Id)
                .Select(g => g.First())
                .OrderBy(r => r.Name)
        ];
    }

    public IReadOnlyList<RepositoryDefinition> GetSavedRepositorySnapshot()
    {
        if (_customRepositories.Count > 0)
        {
            return
            [
                .. _customRepositories
                    .GroupBy(r => r.Id)
                    .Select(g => g.First())
                    .OrderBy(r => r.Name)
            ];
        }

        return BuildSavedRepositorySnapshot();
    }

    public int GetConfiguredRepositoryCount()
    {
        return _configurations.Count;
    }

    public RepositoryDefinition AnalyzeRepository(string repositoryId)
    {
        var repository = GetRepositories(refreshLive: false).FirstOrDefault(r => r.Id == repositoryId);
        return repository ?? new RepositoryDefinition
        {
            Id = repositoryId,
            Name = repositoryId,
            Path = "./",
            DetectedType = DetectedProjectType.Unknown,
            Confidence = 0,
            DetectionSummary = "Repository not yet analyzed. Manual review required.",
            LiveFetchAvailable = false,
            LiveFetchStatus = "Live fetch unavailable.",
            SourceConfigurationId = ResolveSourceConfigurationId(repositoryId)
        };
    }

    public RepositoryRunContext? GetRunContext(string repositoryId)
    {
        if (string.IsNullOrWhiteSpace(repositoryId))
        {
            return null;
        }

        var repository = GetRepositories(refreshLive: false).FirstOrDefault(r => r.Id == repositoryId)
            ?? AnalyzeRepository(repositoryId);
        var sourceConfigurationId = ResolveSourceConfigurationId(repositoryId);
        var config = _configurations.FirstOrDefault(x => CreateRepositoryId(x.Provider, x.RepositoryUrl) == sourceConfigurationId);
        if (config is null || string.IsNullOrWhiteSpace(config.AccessToken))
        {
            return null;
        }

        return new RepositoryRunContext
        {
            SourceConfigurationId = sourceConfigurationId,
            Repository = repository,
            AccessToken = config.AccessToken,
            ProjectReference = !string.IsNullOrWhiteSpace(repository.Path) ? repository.Path : repositoryId
        };
    }

    public async Task<RepositoryDefinition> AnalyzeRepositoryWithProgressAsync(string repositoryId, IProgress<string>? progress, CancellationToken cancellationToken)
    {
        progress?.Report("Initializing repository analysis...");
        await Task.Delay(180, cancellationToken);

        var sourceConfigurationId = ResolveSourceConfigurationId(repositoryId);
        var config = _configurations.FirstOrDefault(x => CreateRepositoryId(x.Provider, x.RepositoryUrl) == sourceConfigurationId);
        if (config is null)
        {
            progress?.Report("No saved source configuration found for this repository.");
            return AnalyzeRepository(repositoryId);
        }

        progress?.Report($"Connecting to {config.Provider} source...");
        await Task.Delay(180, cancellationToken);

        IReadOnlyList<RepositoryDefinition> liveResults;
        try
        {
            liveResults = await FetchRepositoriesForConfigurationAsync(config, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            progress?.Report("Analysis canceled by user.");
            throw;
        }
        catch
        {
            progress?.Report("Live fetch failed. Falling back to saved repository metadata.");
            liveResults = [];
        }

        progress?.Report("Evaluating manifests and repository fingerprint...");
        await Task.Delay(220, cancellationToken);

        if (liveResults.Count > 0)
        {
            var mapped = liveResults
                .Select(repository =>
                {
                    repository.Owner ??= ParseRepositoryOwner(config.RepositoryUrl);
                    repository.Path ??= config.RepositoryUrl;
                    repository.Provider = config.Provider;
                    repository.LiveFetchAvailable = true;
                    repository.LiveFetchStatus = "Live fetch available";
                    repository.SourceConfigurationId = sourceConfigurationId;
                    repository.SourceDisplayName = ParseRepositoryName(config.RepositoryUrl);
                    repository.SourceRepositoryUrl = config.RepositoryUrl;
                    return repository;
                })
                .ToList();

            _customRepositories.RemoveAll(r => string.Equals(r.SourceConfigurationId, sourceConfigurationId, StringComparison.OrdinalIgnoreCase));
            _customRepositories.AddRange(mapped);
            PersistRepositoriesForSource(sourceConfigurationId, mapped);

            var selected = mapped.FirstOrDefault(r => string.Equals(r.Id, repositoryId, StringComparison.OrdinalIgnoreCase)) ?? mapped.First();
            progress?.Report($"Analysis complete. Detected {selected.DetectedType} with {selected.Confidence}% confidence.");
            return selected;
        }

        var fallback = new RepositoryDefinition
        {
            Id = sourceConfigurationId,
            Name = ParseRepositoryName(config.RepositoryUrl),
            Provider = config.Provider,
            Owner = ParseRepositoryOwner(config.RepositoryUrl),
            DefaultBranch = "main",
            Path = config.RepositoryUrl,
            DetectedType = DetectedProjectType.Unknown,
            Confidence = 0,
            DetectionSummary = "Live analysis unavailable. Verify token, network connectivity, and namespace access.",
            DeploymentTarget = "DEV-WEB-01",
            RequirePeerReview = true,
            RequireChangeTicket = true,
            LiveFetchAvailable = false,
            LiveFetchStatus = "Live fetch unavailable (offline, token blocked, namespace empty, or namespace access denied).",
            SourceConfigurationId = sourceConfigurationId,
            SourceDisplayName = ParseRepositoryName(config.RepositoryUrl),
            SourceRepositoryUrl = config.RepositoryUrl
        };

        _customRepositories.RemoveAll(r => string.Equals(r.SourceConfigurationId, sourceConfigurationId, StringComparison.OrdinalIgnoreCase));
        _customRepositories.Add(fallback);
        PersistRepositoriesForSource(sourceConfigurationId, [fallback]);

        progress?.Report("Analysis completed with blocked/offline status.");
        return fallback;
    }

    public (string Yaml, string WorkflowPath, string Branch, string RepositoryName, DateTime DeployedUtc)? GetLatestWorkflowYamlForPipeline(string repositoryId, string repositoryName, string projectOwner)
    {
        using var db = _dbContextFactory.CreateDbContext();

        var deployment = !string.IsNullOrWhiteSpace(repositoryId)
            ? db.WorkflowDeployments
                .AsNoTracking()
                .Where(x => x.RepositoryId == repositoryId)
                .OrderByDescending(x => x.DeployedUtc)
                .FirstOrDefault()
            : db.WorkflowDeployments
                .AsNoTracking()
                .Where(x => x.RepositoryName == repositoryName && x.ProjectOwner == projectOwner)
                .OrderByDescending(x => x.DeployedUtc)
                .FirstOrDefault();

        return deployment is null
            ? null
            : (deployment.YamlContent, deployment.WorkflowPath, deployment.Branch, deployment.RepositoryName, deployment.DeployedUtc);
    }

    public async Task<WorkflowDeploymentResult> DeployWorkflowToRepositoryAsync(
        string repositoryId,
        string yaml,
        string? selectedTemplateId,
        IProgress<string>? progress,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(repositoryId))
        {
            return new WorkflowDeploymentResult
            {
                Success = false,
                Message = "Repository selection is required."
            };
        }

        var repository = GetRepositories(refreshLive: false).FirstOrDefault(r => r.Id == repositoryId)
            ?? AnalyzeRepository(repositoryId);

        var diagnostics = DiagnoseWorkflowYaml(yaml, repository.Provider, selectedTemplateId);
        if (diagnostics.Any(d => d.Severity == WorkflowDiagnosticSeverity.Error))
        {
            return new WorkflowDeploymentResult
            {
                Success = false,
                Message = "YAML contains blocking errors. Resolve diagnostics before deploy."
            };
        }

        var sourceConfigurationId = ResolveSourceConfigurationId(repositoryId);
        var config = _configurations.FirstOrDefault(x => CreateRepositoryId(x.Provider, x.RepositoryUrl) == sourceConfigurationId);
        if (config is null)
        {
            return new WorkflowDeploymentResult
            {
                Success = false,
                Message = "Repository source configuration is missing."
            };
        }

        var template = !string.IsNullOrWhiteSpace(selectedTemplateId)
            ? GetTemplateCatalog().FirstOrDefault(t => t.Id.Equals(selectedTemplateId, StringComparison.OrdinalIgnoreCase)) ?? GetTemplateForRepository(repository)
            : GetTemplateForRepository(repository);

        progress?.Report($"Preparing deployment for {repository.Name}...");
        await Task.Delay(120, cancellationToken);

        var branch = string.IsNullOrWhiteSpace(repository.DefaultBranch) ? "main" : repository.DefaultBranch;
        var workflowPath = repository.Provider == RepositoryProvider.GitLab
            ? ".gitlab-ci.yml"
            : template.WorkflowFile;

        progress?.Report($"Deploying '{workflowPath}' to branch '{branch}'...");

        (bool Success, string Message) result = repository.Provider switch
        {
            RepositoryProvider.GitLab when _gitLabRepositoryService is not null => await _gitLabRepositoryService.UpsertWorkflowFileAsync(
                projectReference: !string.IsNullOrWhiteSpace(repository.Path) ? repository.Path : repositoryId,
                branch: branch,
                workflowPath: workflowPath,
                yaml: yaml,
                accessToken: config.AccessToken,
                cancellationToken: cancellationToken),
            RepositoryProvider.GitHub when _gitHubRepositoryService is not null => await _gitHubRepositoryService.UpsertWorkflowFileAsync(
                repositoryPath: !string.IsNullOrWhiteSpace(repository.Path) ? repository.Path : $"{repository.Owner}/{repository.Name}",
                branch: branch,
                workflowPath: workflowPath,
                yaml: yaml,
                accessToken: config.AccessToken,
                cancellationToken: cancellationToken),
            _ => (false, "Provider deployment is not configured.")
        };

        if (!result.Success)
        {
            progress?.Report("Deployment failed.");
            return new WorkflowDeploymentResult
            {
                Success = false,
                TrackingRecorded = false,
                Message = result.Message,
                WorkflowPath = workflowPath,
                Branch = branch
            };
        }

        progress?.Report("Workflow committed to repository. Recording deployment history...");
        var deployedUtc = DateTime.UtcNow;

        try
        {
            PersistWorkflowDeployment(new WorkflowDeploymentEntity
            {
                SourceConfigurationId = sourceConfigurationId,
                RepositoryId = repository.Id,
                RepositoryName = repository.Name,
                ProjectOwner = repository.Owner,
                Provider = (int)repository.Provider,
                Branch = branch,
                WorkflowPath = workflowPath,
                TemplateId = template.Id,
                YamlContent = yaml,
                DeployedUtc = deployedUtc
            });

            progress?.Report("Deployment completed successfully.");
            return new WorkflowDeploymentResult
            {
                Success = true,
                TrackingRecorded = true,
                Message = result.Message,
                WorkflowPath = workflowPath,
                Branch = branch,
                DeployedUtc = deployedUtc
            };
        }
        catch (Exception ex)
        {
            progress?.Report("Deployment completed, but database tracking failed.");
            return new WorkflowDeploymentResult
            {
                Success = true,
                TrackingRecorded = false,
                Message = $"{result.Message} Workflow deployed, but deployment history could not be recorded: {ex.Message}",
                WorkflowPath = workflowPath,
                Branch = branch,
                DeployedUtc = deployedUtc
            };
        }
    }

    private string ResolveSourceConfigurationId(string repositoryId)
    {
        var customRepository = _customRepositories.FirstOrDefault(x => string.Equals(x.Id, repositoryId, StringComparison.OrdinalIgnoreCase));
        if (customRepository is not null && !string.IsNullOrWhiteSpace(customRepository.SourceConfigurationId))
        {
            return customRepository.SourceConfigurationId;
        }

        if (_configurations.Any(x => string.Equals(CreateRepositoryId(x.Provider, x.RepositoryUrl), repositoryId, StringComparison.OrdinalIgnoreCase)))
        {
            return repositoryId;
        }

        var bySourceId = _customRepositories.FirstOrDefault(x => string.Equals(x.SourceConfigurationId, repositoryId, StringComparison.OrdinalIgnoreCase));
        return bySourceId?.SourceConfigurationId ?? repositoryId;
    }

    private async Task<IReadOnlyList<RepositoryDefinition>> FetchRepositoriesForConfigurationAsync(RepositoryConfiguration config, CancellationToken cancellationToken)
    {
        var (repositories, _) = await FetchRepositoriesForConfigurationWithDiagnosticsAsync(config, cancellationToken);
        return repositories;
    }

    private Task<(IReadOnlyList<RepositoryDefinition> Repositories, string? Error)> FetchRepositoriesForConfigurationWithDiagnosticsAsync(RepositoryConfiguration config, CancellationToken cancellationToken)
    {
        return config.Provider switch
        {
            RepositoryProvider.GitLab when _gitLabRepositoryService is not null => _gitLabRepositoryService.TryGetProjectsForNamespaceAsync(config.RepositoryUrl, config.AccessToken, cancellationToken),
            RepositoryProvider.GitHub when _gitHubRepositoryService is not null => _gitHubRepositoryService.TryGetProjectsForNamespaceAsync(config.RepositoryUrl, config.AccessToken, cancellationToken),
            _ => Task.FromResult<(IReadOnlyList<RepositoryDefinition> Repositories, string? Error)>((Array.Empty<RepositoryDefinition>(), "Provider service is not configured."))
        };
    }

    private void RefreshConfiguredRepositories()
    {
        _customRepositories.Clear();

        foreach (var config in _configurations)
        {
            IReadOnlyList<RepositoryDefinition> results;
            var sourceConfigurationId = CreateRepositoryId(config.Provider, config.RepositoryUrl);
            var fetchBlocked = false;
            var fetchStatus = "Live fetch available";

            try
            {
                results = config.Provider switch
                {
                    RepositoryProvider.GitLab when _gitLabRepositoryService is not null => _gitLabRepositoryService.GetProjectsForNamespaceAsync(config.RepositoryUrl, config.AccessToken).GetAwaiter().GetResult(),
                    RepositoryProvider.GitHub when _gitHubRepositoryService is not null => _gitHubRepositoryService.GetProjectsForNamespaceAsync(config.RepositoryUrl, config.AccessToken).GetAwaiter().GetResult(),
                    _ => []
                };
            }
            catch
            {
                results = [];
                fetchBlocked = true;
                fetchStatus = "Live fetch unavailable (offline, token blocked, or namespace access denied).";
            }

            if (results.Count > 0)
            {
                var sourceResults = new List<RepositoryDefinition>();
                foreach (var repository in results)
                {
                    repository.Owner ??= ParseRepositoryOwner(config.RepositoryUrl);
                    repository.Path ??= config.RepositoryUrl;
                    repository.Provider = config.Provider;
                    repository.LiveFetchAvailable = true;
                    repository.LiveFetchStatus = "Live fetch available";
                    repository.SourceConfigurationId = sourceConfigurationId;
                    repository.SourceDisplayName = ParseRepositoryName(config.RepositoryUrl);
                    repository.SourceRepositoryUrl = config.RepositoryUrl;
                    _customRepositories.Add(repository);
                    sourceResults.Add(repository);
                }

                PersistRepositoriesForSource(sourceConfigurationId, sourceResults);
                continue;
            }

            if (!fetchBlocked)
            {
                fetchBlocked = true;
                fetchStatus = "Live fetch unavailable (offline, token blocked, namespace empty, or namespace access denied).";
            }

            var fallbackRepository = new RepositoryDefinition
            {
                Id = sourceConfigurationId,
                Name = ParseRepositoryName(config.RepositoryUrl),
                Provider = config.Provider,
                Owner = ParseRepositoryOwner(config.RepositoryUrl),
                DefaultBranch = "main",
                Path = config.RepositoryUrl,
                DetectedType = DetectedProjectType.Unknown,
                Confidence = 0,
                DetectionSummary = "Repository added. Click Analyze to inspect the project signature and detection score.",
                DeploymentTarget = "DEV-WEB-01",
                RequirePeerReview = true,
                RequireChangeTicket = true,
                LiveFetchAvailable = !fetchBlocked,
                LiveFetchStatus = fetchStatus,
                SourceConfigurationId = sourceConfigurationId,
                SourceDisplayName = ParseRepositoryName(config.RepositoryUrl),
                SourceRepositoryUrl = config.RepositoryUrl
            };

            _customRepositories.Add(fallbackRepository);
            PersistRepositoriesForSource(sourceConfigurationId, [fallbackRepository]);
        }
    }

    private void DeletePersistedStorageIfPresent()
    {
        if (File.Exists(_storagePath))
        {
            File.Delete(_storagePath);
        }
    }

    private void LoadFromDatabase()
    {
        using var db = _dbContextFactory.CreateDbContext();

        _configurations.Clear();
        var sources = db.RepositorySources.AsNoTracking().ToList();
        foreach (var source in sources)
        {
            _configurations.Add(new RepositoryConfiguration
            {
                Provider = (RepositoryProvider)source.Provider,
                RepositoryUrl = source.RepositoryUrl,
                AccessToken = DecryptToken(source.AccessTokenEncrypted)
            });
        }

        _customRepositories.Clear();
        var projects = db.RepositoryProjects.AsNoTracking().ToList();
        foreach (var project in projects)
        {
            _customRepositories.Add(MapEntityToDefinition(project));
        }
    }

    private void PersistSourceConfiguration(RepositoryConfiguration config, string sourceConfigurationId)
    {
        using var db = _dbContextFactory.CreateDbContext();
        var existing = db.RepositorySources.FirstOrDefault(x => x.SourceConfigurationId == sourceConfigurationId);
        if (existing is null)
        {
            db.RepositorySources.Add(new RepositorySourceEntity
            {
                SourceConfigurationId = sourceConfigurationId,
                Provider = (int)config.Provider,
                RepositoryUrl = config.RepositoryUrl,
                AccessTokenEncrypted = EncryptToken(config.AccessToken),
                CreatedUtc = DateTime.UtcNow,
                UpdatedUtc = DateTime.UtcNow
            });
        }
        else
        {
            existing.Provider = (int)config.Provider;
            existing.RepositoryUrl = config.RepositoryUrl;
            existing.AccessTokenEncrypted = EncryptToken(config.AccessToken);
            existing.UpdatedUtc = DateTime.UtcNow;
        }

        db.SaveChanges();
    }

    private void PersistRepositoriesForSource(string sourceConfigurationId, IEnumerable<RepositoryDefinition> repositories)
    {
        using var db = _dbContextFactory.CreateDbContext();
        var existing = db.RepositoryProjects.Where(x => x.SourceConfigurationId == sourceConfigurationId).ToList();
        if (existing.Count > 0)
        {
            db.RepositoryProjects.RemoveRange(existing);
        }

        var entities = repositories
            .Select(MapDefinitionToEntity)
            .ToList();

        if (entities.Count > 0)
        {
            db.RepositoryProjects.AddRange(entities);
        }

        db.SaveChanges();
    }

    private void RemoveSourceConfiguration(string sourceConfigurationId)
    {
        using var db = _dbContextFactory.CreateDbContext();

        var projects = db.RepositoryProjects.Where(x => x.SourceConfigurationId == sourceConfigurationId).ToList();
        if (projects.Count > 0)
        {
            db.RepositoryProjects.RemoveRange(projects);
        }

        var deployments = db.WorkflowDeployments.Where(x => x.SourceConfigurationId == sourceConfigurationId).ToList();
        if (deployments.Count > 0)
        {
            db.WorkflowDeployments.RemoveRange(deployments);
        }

        var runs = db.WorkflowRuns.Where(x => x.SourceConfigurationId == sourceConfigurationId).ToList();
        if (runs.Count > 0)
        {
            db.WorkflowRuns.RemoveRange(runs);
        }

        var source = db.RepositorySources.FirstOrDefault(x => x.SourceConfigurationId == sourceConfigurationId);
        if (source is not null)
        {
            db.RepositorySources.Remove(source);
        }

        db.SaveChanges();
    }

    private void ClearDatabaseState()
    {
        using var db = _dbContextFactory.CreateDbContext();
        db.WorkflowRuns.RemoveRange(db.WorkflowRuns);
        db.WorkflowDeployments.RemoveRange(db.WorkflowDeployments);
        db.RepositoryProjects.RemoveRange(db.RepositoryProjects);
        db.RepositorySources.RemoveRange(db.RepositorySources);
        db.SaveChanges();
    }

    private void PersistWorkflowDeployment(WorkflowDeploymentEntity deployment)
    {
        using var db = _dbContextFactory.CreateDbContext();
        db.WorkflowDeployments.Add(deployment);
        db.SaveChanges();
    }

    private static RepositoryProjectEntity MapDefinitionToEntity(RepositoryDefinition definition)
    {
        return new RepositoryProjectEntity
        {
            SourceConfigurationId = definition.SourceConfigurationId,
            RepositoryId = definition.Id,
            Name = definition.Name,
            Provider = (int)definition.Provider,
            Owner = definition.Owner,
            DefaultBranch = definition.DefaultBranch,
            Path = definition.Path,
            DetectedType = (int)definition.DetectedType,
            Confidence = definition.Confidence,
            DetectionSummary = definition.DetectionSummary,
            LastUpdated = definition.LastUpdated,
            DeploymentTarget = definition.DeploymentTarget,
            RequirePeerReview = definition.RequirePeerReview,
            RequireChangeTicket = definition.RequireChangeTicket,
            LiveFetchAvailable = definition.LiveFetchAvailable,
            LiveFetchStatus = definition.LiveFetchStatus,
            SourceDisplayName = definition.SourceDisplayName,
            SourceRepositoryUrl = definition.SourceRepositoryUrl,
            FingerprintJson = JsonSerializer.Serialize(definition.Fingerprint)
        };
    }

    private static RepositoryDefinition MapEntityToDefinition(RepositoryProjectEntity entity)
    {
        ProjectFingerprint fingerprint;
        try
        {
            fingerprint = string.IsNullOrWhiteSpace(entity.FingerprintJson)
                ? new ProjectFingerprint()
                : JsonSerializer.Deserialize<ProjectFingerprint>(entity.FingerprintJson) ?? new ProjectFingerprint();
        }
        catch
        {
            fingerprint = new ProjectFingerprint();
        }

        return new RepositoryDefinition
        {
            Id = entity.RepositoryId,
            Name = entity.Name,
            Provider = (RepositoryProvider)entity.Provider,
            Owner = entity.Owner,
            DefaultBranch = entity.DefaultBranch,
            Path = entity.Path,
            DetectedType = (DetectedProjectType)entity.DetectedType,
            Confidence = entity.Confidence,
            DetectionSummary = entity.DetectionSummary,
            Fingerprint = fingerprint,
            LastUpdated = entity.LastUpdated,
            DeploymentTarget = entity.DeploymentTarget,
            RequirePeerReview = entity.RequirePeerReview,
            RequireChangeTicket = entity.RequireChangeTicket,
            LiveFetchAvailable = entity.LiveFetchAvailable,
            LiveFetchStatus = entity.LiveFetchStatus,
            SourceConfigurationId = entity.SourceConfigurationId,
            SourceDisplayName = entity.SourceDisplayName,
            SourceRepositoryUrl = entity.SourceRepositoryUrl
        };
    }

    private static string EncryptToken(string token)
    {
        if (string.IsNullOrWhiteSpace(token))
        {
            return string.Empty;
        }

        try
        {
            using var aes = Aes.Create();
            aes.Key = DeriveEncryptionKey();
            aes.GenerateIV();

            var rawBytes = Encoding.UTF8.GetBytes(token);
            using var encryptor = aes.CreateEncryptor(aes.Key, aes.IV);
            using var output = new MemoryStream();
            output.Write(aes.IV, 0, aes.IV.Length);

            using (var cryptoStream = new CryptoStream(output, encryptor, CryptoStreamMode.Write))
            {
                cryptoStream.Write(rawBytes, 0, rawBytes.Length);
                cryptoStream.FlushFinalBlock();
            }

            return Convert.ToBase64String(output.ToArray());
        }
        catch
        {
            return token;
        }
    }

    private static string DecryptToken(string token)
    {
        if (string.IsNullOrWhiteSpace(token))
        {
            return string.Empty;
        }

        try
        {
            var payload = Convert.FromBase64String(token);
            using var aes = Aes.Create();
            aes.Key = DeriveEncryptionKey();

            var iv = new byte[16];
            Array.Copy(payload, 0, iv, 0, iv.Length);
            aes.IV = iv;

            var ciphertext = new byte[payload.Length - iv.Length];
            Array.Copy(payload, iv.Length, ciphertext, 0, ciphertext.Length);

            using var decryptor = aes.CreateDecryptor(aes.Key, aes.IV);
            using var input = new MemoryStream(ciphertext);
            using var output = new MemoryStream();
            using (var cryptoStream = new CryptoStream(input, decryptor, CryptoStreamMode.Read))
            {
                cryptoStream.CopyTo(output);
            }

            return Encoding.UTF8.GetString(output.ToArray());
        }
        catch
        {
            return token;
        }
    }

    private static byte[] DeriveEncryptionKey()
    {
        var material = $"CICD_PipelineOrchestrator:{Environment.MachineName}:{Environment.UserName}:{Environment.ProcessPath ?? "app"}";
        return SHA256.HashData(Encoding.UTF8.GetBytes(material));
    }

    private static string NormalizeRepositoryUrl(string repositoryUrl)
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

        if (!trimmed.Contains("://", StringComparison.Ordinal))
        {
            trimmed = $"https://{trimmed.TrimStart('/')}";
        }

        if (!Uri.TryCreate(trimmed, UriKind.Absolute, out var uri))
        {
            return string.Empty;
        }

        var path = uri.AbsolutePath.Trim('/');
        if (string.IsNullOrWhiteSpace(path))
        {
            return string.Empty;
        }

        return $"{uri.Scheme}://{uri.Host}/{path}";
    }

    private List<RepositoryDefinition> BuildSavedRepositorySnapshot()
    {
        return
        [
            .. _configurations
                .Select(config => new RepositoryDefinition
                {
                    Id = CreateRepositoryId(config.Provider, config.RepositoryUrl),
                    Name = ParseRepositoryName(config.RepositoryUrl),
                    Provider = config.Provider,
                    Owner = ParseRepositoryOwner(config.RepositoryUrl),
                    DefaultBranch = "main",
                    Path = config.RepositoryUrl,
                    DetectedType = DetectedProjectType.Unknown,
                    Confidence = 0,
                    DetectionSummary = "Repository saved. Click Analyze to run live detection and load repository details.",
                    DeploymentTarget = "DEV-WEB-01",
                    RequirePeerReview = true,
                    RequireChangeTicket = true,
                    LiveFetchAvailable = false,
                    LiveFetchStatus = "Live fetch not attempted yet.",
                    SourceConfigurationId = CreateRepositoryId(config.Provider, config.RepositoryUrl),
                    SourceDisplayName = ParseRepositoryName(config.RepositoryUrl),
                    SourceRepositoryUrl = config.RepositoryUrl
                })
                .GroupBy(r => r.Id)
                .Select(g => g.First())
                .OrderBy(r => r.Name)
        ];
    }

    private static string ParseRepositoryName(string repositoryUrl)
    {
        var trimmed = NormalizeRepositoryUrl(repositoryUrl);
        if (string.IsNullOrWhiteSpace(trimmed))
        {
            return "repository";
        }

        var segments = trimmed.Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return segments.LastOrDefault() ?? "repository";
    }

    private static string ParseRepositoryOwner(string repositoryUrl)
    {
        var trimmed = NormalizeRepositoryUrl(repositoryUrl);
        if (string.IsNullOrWhiteSpace(trimmed))
        {
            return "unknown";
        }

        var segments = trimmed.Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return segments.Length > 1 ? segments[^2] : segments.FirstOrDefault() ?? "unknown";
    }

    private static string CreateRepositoryId(RepositoryProvider provider, string repositoryUrl)
    {
        var normalized = NormalizeRepositoryUrl(repositoryUrl);
        var value = normalized.Replace("https://", string.Empty).Replace("http://", string.Empty).Replace("/", "-");
        return $"{provider.ToString().ToLowerInvariant()}-{value}";
    }

    private sealed class RepositoryConfiguration
    {
        public RepositoryProvider Provider { get; set; }
        public string RepositoryUrl { get; set; } = string.Empty;
        public string AccessToken { get; set; } = string.Empty;
    }

    public IReadOnlyList<PipelineTemplateDefinition> GetTemplateCatalog()
    {
        return
        [
            new()
            {
                Id = "dotnet-api",
                Name = "ASP.NET Core API",
                WorkflowFile = ".github/workflows/ci-cd.yml",
                Trigger = "push",
                Description = "Build, test, publish, and promote .NET API artifacts across Dev/UAT/Prod.",
                TemplateText = GetDotNetTemplate()
            },
            new()
            {
                Id = "dotnet-worker",
                Name = "DotNet Worker",
                WorkflowFile = ".github/workflows/worker-ci-cd.yml",
                Trigger = "push",
                Description = "Background worker validation with artifact retention and scheduled deployment checks.",
                TemplateText = GetWorkerTemplate()
            },
            new()
            {
                Id = "react-web",
                Name = "React Web App",
                WorkflowFile = ".github/workflows/frontend-ci-cd.yml",
                Trigger = "push",
                Description = "Frontend build, lint, static output publication, and deployment approval workflow.",
                TemplateText = GetReactTemplate()
            },
            new()
            {
                Id = "gitlab-dotnet-api",
                Name = "GitLab .NET API",
                WorkflowFile = ".gitlab-ci.yml",
                Trigger = "push",
                Description = "GitLab CI pipeline for .NET API build, publish, and deployment across environments.",
                TemplateText = GetGitLabDotNetTemplate()
            },
            new()
            {
                Id = "gitlab-dotnet-worker",
                Name = "GitLab .NET Worker",
                WorkflowFile = ".gitlab-ci.yml",
                Trigger = "push",
                Description = "GitLab CI pipeline for .NET worker build, publish, and artifact retention.",
                TemplateText = GetGitLabDotNetWorkerTemplate()
            },
            new()
            {
                Id = "gitlab-react-web",
                Name = "GitLab React Web",
                WorkflowFile = ".gitlab-ci.yml",
                Trigger = "push",
                Description = "GitLab CI pipeline for a React frontend build and deployment artifact promotion.",
                TemplateText = GetGitLabReactTemplate()
            }
        ];
    }

    public PipelineTemplateDefinition GetTemplateForRepository(RepositoryDefinition repository)
    {
        var catalog = GetTemplateCatalog();

        if (repository.Provider == RepositoryProvider.GitLab)
        {
            return repository.DetectedType switch
            {
                DetectedProjectType.AspNetCoreApi => catalog.First(t => t.Id == "gitlab-dotnet-api"),
                DetectedProjectType.ReactWeb => catalog.First(t => t.Id == "gitlab-react-web"),
                DetectedProjectType.DotNetWorker => catalog.First(t => t.Id == "gitlab-dotnet-worker"),
                _ => catalog.First(t => t.Id == "gitlab-dotnet-api")
            };
        }

        return repository.DetectedType switch
        {
            DetectedProjectType.AspNetCoreApi => catalog.First(t => t.Id == "dotnet-api"),
            DetectedProjectType.DotNetWorker => catalog.First(t => t.Id == "dotnet-worker"),
            DetectedProjectType.ReactWeb => catalog.First(t => t.Id == "react-web"),
            DetectedProjectType.AngularApp => catalog.First(t => t.Id == "react-web"),
            DetectedProjectType.JavaMaven => catalog.First(t => t.Id == "react-web"),
            _ => catalog[0]
        };
    }

    public string GeneratePreview(RepositoryDefinition repository)
    {
        return GeneratePreview(repository, selectedTemplateId: null, targetEnvironment: null);
    }

    public string GeneratePreview(RepositoryDefinition repository, string? selectedTemplateId, string? targetEnvironment)
    {
        var catalog = GetTemplateCatalog();
        var template = !string.IsNullOrWhiteSpace(selectedTemplateId)
            ? catalog.FirstOrDefault(t => t.Id.Equals(selectedTemplateId, StringComparison.OrdinalIgnoreCase)) ?? GetTemplateForRepository(repository)
            : GetTemplateForRepository(repository);

        var environment = string.IsNullOrWhiteSpace(targetEnvironment)
            ? (repository.RequirePeerReview ? "uat" : "dev")
            : targetEnvironment.Trim().ToLowerInvariant();

        return $"{template.TemplateText}\n\n# Generated for {repository.Name}\n# Selected template: {template.Name} ({template.Id})\n# Source provider: {repository.Provider}\n# Detected project: {repository.DetectedType}\n# Deployment target: {repository.DeploymentTarget}\n# Promotion path: dev -> {environment} -> production";
    }

    public IReadOnlyList<WorkflowDiagnosticIssue> DiagnoseWorkflowYaml(string yaml, RepositoryProvider provider, string? selectedTemplateId)
    {
        var issues = new List<WorkflowDiagnosticIssue>();
        var suggestedTemplateId = GetProviderMatchedTemplateId(provider, selectedTemplateId);

        if (string.IsNullOrWhiteSpace(yaml))
        {
            issues.Add(new WorkflowDiagnosticIssue
            {
                Severity = WorkflowDiagnosticSeverity.Error,
                Message = "YAML content is empty.",
                Detail = "Generate a template or paste workflow YAML before testing."
            });
            return issues;
        }

        var text = yaml.Replace("\r\n", "\n", StringComparison.Ordinal);

        if (text.Contains('\t'))
        {
            issues.Add(new WorkflowDiagnosticIssue
            {
                Severity = WorkflowDiagnosticSeverity.Warning,
                Message = "Tab indentation detected.",
                Detail = "YAML parsers typically require spaces instead of tabs."
            });
        }

        if (PotentialSecretRegex().IsMatch(text))
        {
            issues.Add(new WorkflowDiagnosticIssue
            {
                Severity = WorkflowDiagnosticSeverity.Warning,
                Message = "Possible secret value found in YAML.",
                Detail = "Move credentials to CI/CD secrets or protected variables."
            });
        }

        var hasGitHubMarkers = text.Contains("jobs:", StringComparison.OrdinalIgnoreCase) && text.Contains("on:", StringComparison.OrdinalIgnoreCase);
        var hasGitLabMarkers = text.Contains("stages:", StringComparison.OrdinalIgnoreCase);

        if (provider == RepositoryProvider.GitLab)
        {
            if (!hasGitLabMarkers)
            {
                issues.Add(new WorkflowDiagnosticIssue
                {
                    Severity = WorkflowDiagnosticSeverity.Error,
                    Message = "Missing GitLab pipeline structure.",
                    Detail = "Expected at least a 'stages:' section for .gitlab-ci.yml workflows.",
                        Recommendation = string.IsNullOrWhiteSpace(suggestedTemplateId)
                            ? "Use a GitLab-compatible template (.gitlab-ci.yml structure)."
                            : $"Switch to '{suggestedTemplateId}' then re-test.",
                        SuggestedTemplateId = suggestedTemplateId ?? string.Empty
                    });
            }

            if (text.Contains("stage: test", StringComparison.OrdinalIgnoreCase) && !GitLabHasDeclaredStage(text, "test"))
            {
                issues.Add(new WorkflowDiagnosticIssue
                {
                    Severity = WorkflowDiagnosticSeverity.Error,
                    Message = "GitLab test stage is not declared.",
                    Detail = "A job is assigned to stage 'test', but the stages list does not include 'test'. GitLab will reject this pipeline.",
                    Recommendation = "Add 'test' to the stages list before publish/deploy.",
                    SuggestedYamlSnippet = GetGitLabTestStageDeclarationSnippet()
                });
            }

            if (!text.Contains("script:", StringComparison.OrdinalIgnoreCase))
            {
                issues.Add(new WorkflowDiagnosticIssue
                {
                    Severity = WorkflowDiagnosticSeverity.Warning,
                    Message = "No script steps detected.",
                    Detail = "GitLab jobs usually need script commands to execute build/test/deploy tasks."
                });
            }
        }
        else
        {
            if (!hasGitHubMarkers)
            {
                issues.Add(new WorkflowDiagnosticIssue
                {
                    Severity = WorkflowDiagnosticSeverity.Error,
                    Message = "Missing GitHub Actions workflow structure.",
                    Detail = "Expected both 'on:' and 'jobs:' sections.",
                        Recommendation = string.IsNullOrWhiteSpace(suggestedTemplateId)
                            ? "Use a GitHub Actions compatible template with on/jobs sections."
                            : $"Switch to '{suggestedTemplateId}' then re-test.",
                        SuggestedTemplateId = suggestedTemplateId ?? string.Empty
                    });
            }

            if (!text.Contains("runs-on:", StringComparison.OrdinalIgnoreCase))
            {
                issues.Add(new WorkflowDiagnosticIssue
                {
                    Severity = WorkflowDiagnosticSeverity.Warning,
                    Message = "No runner selected.",
                    Detail = "Each GitHub Actions job should define 'runs-on'."
                });
            }
        }

        if (!text.Contains("build", StringComparison.OrdinalIgnoreCase))
        {
            issues.Add(new WorkflowDiagnosticIssue
            {
                Severity = WorkflowDiagnosticSeverity.Warning,
                Message = "No build stage detected.",
                Detail = "Consider adding explicit build steps to verify compile/package output."
            });
        }

        if (!text.Contains("test", StringComparison.OrdinalIgnoreCase))
        {
            issues.Add(new WorkflowDiagnosticIssue
            {
                Severity = WorkflowDiagnosticSeverity.Warning,
                Message = "No test stage detected.",
                Detail = "Automated tests are recommended before deployment stages.",
                Recommendation = "Add a test job/stage and run it before publish/deploy.",
                SuggestedYamlSnippet = provider == RepositoryProvider.GitLab
                    ? GetGitLabTestStageSnippet()
                    : GetGitHubTestJobSnippet()
            });
        }

        if (!string.IsNullOrWhiteSpace(selectedTemplateId) && provider == RepositoryProvider.GitLab && selectedTemplateId.StartsWith("dotnet-", StringComparison.OrdinalIgnoreCase))
        {
            issues.Add(new WorkflowDiagnosticIssue
            {
                Severity = WorkflowDiagnosticSeverity.Warning,
                Message = "Selected template may not match provider.",
                Detail = "GitLab repositories usually use templates prefixed with 'gitlab-'.",
                Recommendation = string.IsNullOrWhiteSpace(suggestedTemplateId)
                    ? "Switch to a GitLab template (gitlab-*)."
                    : $"Switch to '{suggestedTemplateId}'.",
                SuggestedTemplateId = suggestedTemplateId ?? string.Empty
            });
        }

        if (!string.IsNullOrWhiteSpace(selectedTemplateId) && provider != RepositoryProvider.GitLab && selectedTemplateId.StartsWith("gitlab-", StringComparison.OrdinalIgnoreCase))
        {
            issues.Add(new WorkflowDiagnosticIssue
            {
                Severity = WorkflowDiagnosticSeverity.Warning,
                Message = "Selected template may not match provider.",
                Detail = "GitHub repositories usually use non-GitLab template IDs.",
                Recommendation = string.IsNullOrWhiteSpace(suggestedTemplateId)
                    ? "Switch to a GitHub/non-GitLab template."
                    : $"Switch to '{suggestedTemplateId}'.",
                SuggestedTemplateId = suggestedTemplateId ?? string.Empty
            });
        }

        if (issues.All(i => i.Severity != WorkflowDiagnosticSeverity.Error))
        {
            issues.Add(new WorkflowDiagnosticIssue
            {
                Severity = WorkflowDiagnosticSeverity.Info,
                Message = "Validation passed with no blocking errors.",
                Detail = "You can proceed to commit this workflow after addressing warnings, if any.",
                Recommendation = "Optional: run a dry-run/CI lint in your provider before merge."
            });
        }

        return issues;
    }

    private static string? GetProviderMatchedTemplateId(RepositoryProvider provider, string? selectedTemplateId)
    {
        if (string.IsNullOrWhiteSpace(selectedTemplateId))
        {
            return null;
        }

        var selected = selectedTemplateId.Trim();

        if (provider == RepositoryProvider.GitLab)
        {
            return selected switch
            {
                "dotnet-worker" => "gitlab-dotnet-worker",
                "dotnet-api" => "gitlab-dotnet-api",
                "react-web" => "gitlab-react-web",
                _ => selected.StartsWith("gitlab-", StringComparison.OrdinalIgnoreCase) ? selected : null
            };
        }

        if (provider == RepositoryProvider.GitHub)
        {
            return selected switch
            {
                "gitlab-dotnet-worker" => "dotnet-worker",
                "gitlab-dotnet-api" => "dotnet-api",
                "gitlab-react-web" => "react-web",
                _ => selected.StartsWith("gitlab-", StringComparison.OrdinalIgnoreCase) ? null : selected
            };
        }

        return null;
    }

    [GeneratedRegex(@"(?i)(glpat-|ghp_|password\s*:|token\s*:)")]
    private static partial Regex PotentialSecretRegex();

    private static string GetGitHubTestJobSnippet()
    {
        return """
# Add this test job to your workflow
  test:
    runs-on: ubuntu-latest
    needs: build
    steps:
      - uses: actions/checkout@v4
      - uses: actions/setup-dotnet@v4
        with:
          dotnet-version: '8.0.x'
      - name: Run tests
        run: dotnet test --configuration Release --no-build
""";
    }

    private static string GetGitLabTestStageSnippet()
    {
        return """
test:
  stage: test
  image: mcr.microsoft.com/dotnet/sdk:8.0
  script:
    - dotnet test --configuration Release --no-build
""";
    }

    private static string GetGitLabTestStageDeclarationSnippet()
    {
        return """
stages:
  - restore
  - build
  - test
  - publish
""";
    }

    private static bool GitLabHasDeclaredStage(string yaml, string stageName)
    {
        var lines = yaml.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        var inStages = false;

        foreach (var rawLine in lines)
        {
            var line = rawLine.TrimEnd();
            var trimmed = line.Trim();
            if (!inStages)
            {
                if (trimmed.Equals("stages:", StringComparison.OrdinalIgnoreCase))
                {
                    inStages = true;
                }

                continue;
            }

            if (string.IsNullOrWhiteSpace(trimmed))
            {
                continue;
            }

            if (!char.IsWhiteSpace(line[0]) && !trimmed.StartsWith("-", StringComparison.Ordinal))
            {
                break;
            }

            if (trimmed.StartsWith("-", StringComparison.Ordinal))
            {
                var declaredStage = trimmed[1..].Trim();
                if (declaredStage.Equals(stageName, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
        }

        return false;
    }

    private static string GetDotNetTemplate()
    {
        return """
name: ASP.NET Core CI/CD

on:
  push:
    branches: [develop, main]
  workflow_dispatch:

jobs:
  build:
    runs-on: ubuntu-latest
    steps:
      - uses: actions/checkout@v4
      - uses: actions/setup-dotnet@v4
        with:
          dotnet-version: '8.0.x'
      - name: Restore
        run: dotnet restore
      - name: Build
        run: dotnet build --configuration Release --no-restore
      - name: Test
        run: dotnet test --configuration Release --no-build
      - name: Publish
        run: dotnet publish src/CustomerApi/CustomerApi.csproj -c Release -o ./publish
      - name: Upload artifact
        uses: actions/upload-artifact@v4
        with:
          name: application
          path: ./publish

  deploy-dev:
    if: github.ref == 'refs/heads/develop'
    needs: build
    runs-on: [self-hosted, windows, dev]
    environment: development
    steps:
      - uses: actions/download-artifact@v4
        with:
          name: application
          path: ./publish
      - name: Deploy to Dev
        shell: pwsh
        run: ./deploy/Deploy-IIS.ps1 -PackagePath ./publish
""";
    }

    private static string GetWorkerTemplate()
    {
        return """
name: .NET Worker Deployment

on:
  push:
    branches: [main]
  workflow_dispatch:

jobs:
  build:
    runs-on: ubuntu-latest
    steps:
      - uses: actions/checkout@v4
      - uses: actions/setup-dotnet@v4
        with:
          dotnet-version: '8.0.x'
      - name: Restore
        run: dotnet restore
      - name: Build
        run: dotnet build --configuration Release --no-restore
      - name: Publish
        run: dotnet publish src/PayrollWorker/PayrollWorker.csproj -c Release -o ./publish
      - name: Upload artifact
        uses: actions/upload-artifact@v4
        with:
          name: worker
          path: ./publish
""";
    }

    private static string GetReactTemplate()
    {
        return """
name: React Frontend CI/CD

on:
  push:
    branches: [main]
  workflow_dispatch:

jobs:
  build:
    runs-on: ubuntu-latest
    steps:
      - uses: actions/checkout@v4
      - uses: actions/setup-node@v4
        with:
          node-version: '20'
      - name: Install dependencies
        run: npm ci
      - name: Build frontend
        run: npm run build
      - name: Upload static artifact
        uses: actions/upload-artifact@v4
        with:
          name: frontend-build
          path: ./dist
""";
    }

    private static string GetGitLabDotNetTemplate()
    {
        return """
stages:
  - restore
  - build
  - test
  - publish
  - deploy

variables:
  DOTNET_CLI_TELEMETRY_OPTOUT: "1"
  BUILD_PATH: "src/CustomerApi/CustomerApi.csproj"

restore:
  stage: restore
  image: mcr.microsoft.com/dotnet/sdk:8.0
  script:
    - dotnet restore

build:
  stage: build
  image: mcr.microsoft.com/dotnet/sdk:8.0
  script:
    - dotnet build --configuration Release --no-restore

unit_test:
  stage: test
  image: mcr.microsoft.com/dotnet/sdk:8.0
  script:
    - dotnet test --configuration Release --no-build --logger "junit;LogFileName=results.xml"

publish:
  stage: publish
  image: mcr.microsoft.com/dotnet/sdk:8.0
  script:
    - dotnet publish $BUILD_PATH -c Release -o ./publish
  artifacts:
    paths:
      - ./publish

deploy_dev:
  stage: deploy
  environment: dev
  only:
    - develop
  script:
    - echo "Deploying immutable artifact to DEV server"
    - ./deploy/Deploy-IIS.ps1 -PackagePath ./publish
""";
    }

    private static string GetGitLabDotNetWorkerTemplate()
    {
        return """
stages:
  - restore
  - build
  - test
  - publish

variables:
  DOTNET_CLI_TELEMETRY_OPTOUT: "1"
  BUILD_PATH: "src/PayrollWorker/PayrollWorker.csproj"

restore:
  stage: restore
  image: mcr.microsoft.com/dotnet/sdk:8.0
  script:
    - dotnet restore

build:
  stage: build
  image: mcr.microsoft.com/dotnet/sdk:8.0
  script:
    - dotnet build --configuration Release --no-restore

unit_test:
  stage: test
  image: mcr.microsoft.com/dotnet/sdk:8.0
  script:
    - dotnet test --configuration Release --no-build

publish:
  stage: publish
  image: mcr.microsoft.com/dotnet/sdk:8.0
  script:
    - dotnet publish $BUILD_PATH -c Release -o ./publish
  artifacts:
    paths:
      - ./publish
""";
    }

    private static string GetGitLabReactTemplate()
    {
        return """
stages:
  - install
  - build
  - package
  - deploy

image: node:20

install:
  stage: install
  script:
    - npm ci

build:
  stage: build
  script:
    - npm run build

package:
  stage: package
  script:
    - tar -czf frontend-dist.tar.gz dist
  artifacts:
    paths:
      - frontend-dist.tar.gz

deploy_dev:
  stage: deploy
  environment: dev
  only:
    - main
  script:
    - echo "Publishing frontend artifact to staging environment"
""";
    }
}

