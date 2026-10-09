using System.Text.Json;
using CICD_PipelineOrchestrator.Data;
using CICD_PipelineOrchestrator.Data.Entities;
using CICD_PipelineOrchestrator.Models;
using Microsoft.EntityFrameworkCore;

namespace CICD_PipelineOrchestrator.Services;

public sealed class PipelineService(
    IDbContextFactory<RepositoryCatalogDbContext> dbContextFactory,
    RepositoryCatalogService repositoryCatalogService,
    GitLabRepositoryService? gitLabRepositoryService)
{
    private readonly List<PipelineDefinition> _pipelines = [];
    private readonly List<PipelineActivity> _activities = [];
    private readonly HashSet<int> _loadedDeploymentIds = [];
    private readonly HashSet<int> _loadedRunIds = [];
    private readonly List<DateTime> _deploymentRuns = [];
    private readonly Dictionary<int, CancellationTokenSource> _runPollingTokens = [];

    public event Action? Changed;

    public IReadOnlyList<PipelineDefinition> GetPipelines() => _pipelines.AsReadOnly();

    public PipelineSummary GetSummary()
    {
        var totalPipelines = _pipelines.Count;
        var activeRuns = _pipelines.Count(p => p.Status == PipelineStatus.Running);
        var deploymentsToday = _deploymentRuns.Count(d => d.Date == DateTime.UtcNow.Date);
        var successRate = _pipelines.Count == 0
            ? 0
            : (int)Math.Round(_pipelines.Average(p => p.SuccessRate));

        return new PipelineSummary
        {
            TotalPipelines = totalPipelines,
            ActiveRuns = activeRuns,
            DeploymentsToday = deploymentsToday,
            SuccessRate = successRate
        };
    }

    public IReadOnlyList<PipelineActivity> GetRecentActivity() => [.. _activities.OrderByDescending(a => a.Timestamp).Take(10)];

    public async Task TogglePipelineStatusAsync(int id)
    {
        var pipeline = _pipelines.FirstOrDefault(p => p.Id == id);
        if (pipeline is null)
        {
            return;
        }

        if (pipeline.Status == PipelineStatus.Running)
        {
            await CancelPipelineRunAsync(id);
            return;
        }

        await StartPipelineRunAsync(id);
    }

    public async Task StartPipelineRunAsync(int id)
    {
        var pipeline = _pipelines.FirstOrDefault(p => p.Id == id);
        if (pipeline is null || pipeline.Status == PipelineStatus.Running)
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(pipeline.RepositoryId))
        {
            pipeline.Status = PipelineStatus.Warning;
            AppendConsoleLine(pipeline, "This pipeline is not linked to a saved repository. Deploy the YAML to GitLab first.");
            AddActivity("Pipeline blocked", $"{pipeline.Name} cannot start because it is not linked to a saved repository.", "warning");
            NotifyStateChanged();
            return;
        }

        var runContext = repositoryCatalogService.GetRunContext(pipeline.RepositoryId);
        if (runContext is null)
        {
            pipeline.Status = PipelineStatus.Warning;
            AppendConsoleLine(pipeline, "Repository runtime context is unavailable. Reconnect the repository source and try again.");
            AddActivity("Pipeline blocked", $"{pipeline.Name} could not load repository credentials for execution.", "warning");
            NotifyStateChanged();
            return;
        }

        if (runContext.Repository.Provider != RepositoryProvider.GitLab || gitLabRepositoryService is null)
        {
            pipeline.Status = PipelineStatus.Warning;
            AppendConsoleLine(pipeline, "Real pipeline runs are currently supported only for GitLab repositories.");
            AddActivity("Pipeline blocked", $"{pipeline.Name} is not on a GitLab source that supports real pipeline execution.", "warning");
            NotifyStateChanged();
            return;
        }

        if (_runPollingTokens.ContainsKey(pipeline.Id))
        {
            return;
        }

        var branch = string.IsNullOrWhiteSpace(pipeline.Branch) ? runContext.Repository.DefaultBranch : pipeline.Branch;
        pipeline.Trigger = "GitLab";
        pipeline.Branch = string.IsNullOrWhiteSpace(branch) ? "main" : branch.Trim();
        pipeline.Status = PipelineStatus.Running;
        pipeline.LastRun = DateTime.UtcNow;
        pipeline.Stages = CreateQueuedStages();
        pipeline.ConsoleOutput = [];
        pipeline.ExternalPipelineId = null;
        pipeline.ExternalPipelineUrl = string.Empty;
        AppendConsoleLine(pipeline, $"Triggering GitLab pipeline for {pipeline.Name} on branch {pipeline.Branch}...");
        NotifyStateChanged();

        var triggerResult = await gitLabRepositoryService.StartPipelineAsync(runContext.ProjectReference, pipeline.Branch, runContext.AccessToken);
        if (!triggerResult.Success)
        {
            pipeline.Status = PipelineStatus.Failed;
            AppendConsoleLine(pipeline, triggerResult.Message);
            AddActivity("Pipeline failed to start", $"{pipeline.Name} could not be started in GitLab.", "danger");
            NotifyStateChanged();
            return;
        }

        pipeline.ExternalPipelineId = triggerResult.PipelineId;
        pipeline.ExternalPipelineUrl = triggerResult.WebUrl;
        AppendConsoleLine(pipeline, triggerResult.Message);
        if (!string.IsNullOrWhiteSpace(triggerResult.WebUrl))
        {
            AppendConsoleLine(pipeline, $"GitLab pipeline URL: {triggerResult.WebUrl}");
        }

        var runEntity = PersistWorkflowRun(new WorkflowRunEntity
        {
            SourceConfigurationId = runContext.SourceConfigurationId,
            RepositoryId = runContext.Repository.Id,
            RepositoryName = runContext.Repository.Name,
            ProjectOwner = runContext.Repository.Owner,
            Provider = (int)runContext.Repository.Provider,
            Branch = pipeline.Branch,
            Status = triggerResult.Status,
            GitLabPipelineId = triggerResult.PipelineId,
            GitLabWebUrl = triggerResult.WebUrl,
            GitLabJobIdsJson = "[]",
            LogOutput = string.Join(Environment.NewLine, pipeline.ConsoleOutput),
            StartedUtc = DateTime.UtcNow,
            UpdatedUtc = DateTime.UtcNow
        });

        _loadedRunIds.Add(runEntity.Id);
        AddActivity("Pipeline started", $"{pipeline.Name} started GitLab pipeline #{triggerResult.PipelineId}.", "neutral");
        StartPollingForRun(pipeline, runContext, runEntity);
        NotifyStateChanged();
    }

    public async Task CancelPipelineRunAsync(int id)
    {
        var pipeline = _pipelines.FirstOrDefault(p => p.Id == id);
        if (pipeline is null || string.IsNullOrWhiteSpace(pipeline.RepositoryId))
        {
            return;
        }

        var runContext = repositoryCatalogService.GetRunContext(pipeline.RepositoryId);
        if (runContext is null || runContext.Repository.Provider != RepositoryProvider.GitLab || gitLabRepositoryService is null)
        {
            AppendConsoleLine(pipeline, "Cancel is unavailable because the GitLab run context could not be resolved.");
            NotifyStateChanged();
            return;
        }

        var activeRun = GetLatestActiveRun(pipeline.RepositoryId);
        if (activeRun is null)
        {
            AppendConsoleLine(pipeline, "No active GitLab pipeline run was found to cancel.");
            NotifyStateChanged();
            return;
        }

        AppendConsoleLine(pipeline, $"Requesting cancellation for GitLab pipeline #{activeRun.GitLabPipelineId}...");
        NotifyStateChanged();

        var cancelResult = await gitLabRepositoryService.CancelPipelineAsync(runContext.ProjectReference, activeRun.GitLabPipelineId, runContext.AccessToken);
        AppendConsoleLine(pipeline, cancelResult.Message);

        if (cancelResult.Success)
        {
            UpdateWorkflowRunStatus(activeRun.Id, "canceling", string.Join(Environment.NewLine, pipeline.ConsoleOutput));
            AddActivity("Pipeline cancel requested", $"{pipeline.Name} cancellation requested in GitLab.", "warning");
        }
        else
        {
            AddActivity("Pipeline cancel failed", $"{pipeline.Name} could not be canceled in GitLab.", "danger");
        }

        NotifyStateChanged();
    }

    public void AddPipeline(string repositoryId, string name, string project, string branch, string trigger)
    {
        if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(project))
        {
            return;
        }

        var existingPipeline = _pipelines.FirstOrDefault(p =>
            (!string.IsNullOrWhiteSpace(repositoryId) && p.RepositoryId.Equals(repositoryId, StringComparison.OrdinalIgnoreCase))
            || (p.Name.Equals(name, StringComparison.OrdinalIgnoreCase) && p.Project.Equals(project, StringComparison.OrdinalIgnoreCase)));

        if (existingPipeline is not null)
        {
            existingPipeline.RepositoryId = string.IsNullOrWhiteSpace(repositoryId) ? existingPipeline.RepositoryId : repositoryId;
            existingPipeline.Branch = string.IsNullOrWhiteSpace(branch) ? existingPipeline.Branch : branch.Trim();
            existingPipeline.Trigger = string.IsNullOrWhiteSpace(trigger) ? existingPipeline.Trigger : trigger.Trim();
            NotifyStateChanged();
            return;
        }

        var newPipeline = new PipelineDefinition
        {
            Id = _pipelines.Count == 0 ? 1 : _pipelines.Max(p => p.Id) + 1,
            RepositoryId = repositoryId,
            Name = name.Trim(),
            Project = project.Trim(),
            Branch = string.IsNullOrWhiteSpace(branch) ? "main" : branch.Trim(),
            Trigger = string.IsNullOrWhiteSpace(trigger) ? "Manual" : trigger.Trim(),
            Status = PipelineStatus.Healthy,
            LastRun = DateTime.UtcNow,
            SuccessRate = 100,
            Stages = CreateQueuedStages(),
            ConsoleOutput = []
        };

        _pipelines.Add(newPipeline);
        AddActivity("Pipeline created", $"{newPipeline.Name} generated for {newPipeline.Project} on branch {newPipeline.Branch}.", "success");
        NotifyStateChanged();
    }

    public void ReloadFromDatabase()
    {
        using var db = dbContextFactory.CreateDbContext();
        var changed = false;

        var deployments = db.WorkflowDeployments
            .AsNoTracking()
            .OrderBy(x => x.DeployedUtc)
            .ToList();

        foreach (var deployment in deployments)
        {
            if (_loadedDeploymentIds.Contains(deployment.Id))
            {
                continue;
            }

            ApplyDeployment(deployment);
            _loadedDeploymentIds.Add(deployment.Id);
            _deploymentRuns.Add(deployment.DeployedUtc);
            changed = true;
        }

        var runs = db.WorkflowRuns
            .AsNoTracking()
            .OrderBy(x => x.StartedUtc)
            .ToList();

        foreach (var run in runs)
        {
            if (_loadedRunIds.Contains(run.Id))
            {
                continue;
            }

            ApplyRun(run);
            _loadedRunIds.Add(run.Id);
            changed = true;
        }

        foreach (var activeRun in runs.Where(run => IsActiveRunStatus(run.Status)))
        {
            var pipeline = _pipelines.FirstOrDefault(p => p.RepositoryId.Equals(activeRun.RepositoryId, StringComparison.OrdinalIgnoreCase));
            var runContext = repositoryCatalogService.GetRunContext(activeRun.RepositoryId);
            if (pipeline is not null && runContext is not null)
            {
                StartPollingForRun(pipeline, runContext, activeRun);
            }
        }

        if (changed)
        {
            NotifyStateChanged();
        }
    }

    private void ApplyDeployment(WorkflowDeploymentEntity deployment)
    {
        var pipeline = GetOrCreatePipeline(deployment.RepositoryId, deployment.RepositoryName, deployment.ProjectOwner);
        pipeline.RepositoryId = deployment.RepositoryId;
        pipeline.Branch = string.IsNullOrWhiteSpace(deployment.Branch) ? pipeline.Branch : deployment.Branch;
        pipeline.Trigger = "Deployed";
        pipeline.Status = PipelineStatus.Healthy;
        pipeline.LastRun = deployment.DeployedUtc;
        pipeline.SuccessRate = 100;
        pipeline.Stages = CreateSuccessfulStages();
        pipeline.ConsoleOutput =
        [
            $"[{deployment.DeployedUtc.ToLocalTime():HH:mm:ss}] Workflow deployed successfully.",
            $"[{deployment.DeployedUtc.ToLocalTime():HH:mm:ss}] Committed {deployment.WorkflowPath} to branch {deployment.Branch}."
        ];

        AddActivity("Workflow deployed", $"{deployment.RepositoryName} committed {deployment.WorkflowPath} to branch {pipeline.Branch}.", "success", deployment.DeployedUtc);
    }

    private void ApplyRun(WorkflowRunEntity run)
    {
        var pipeline = GetOrCreatePipeline(run.RepositoryId, run.RepositoryName, run.ProjectOwner);
        pipeline.RepositoryId = run.RepositoryId;
        pipeline.Branch = string.IsNullOrWhiteSpace(run.Branch) ? pipeline.Branch : run.Branch;
        pipeline.Trigger = "GitLab";
        pipeline.Status = MapPipelineStatus(run.Status);
        pipeline.LastRun = run.UpdatedUtc == default ? run.StartedUtc : run.UpdatedUtc;
        pipeline.ExternalPipelineId = run.GitLabPipelineId;
        pipeline.ExternalPipelineUrl = run.GitLabWebUrl;
        pipeline.Stages = CreateStagesFromRunStatus(run.Status);
        pipeline.ConsoleOutput = ParseLogOutput(run.LogOutput, run.StartedUtc, run.GitLabPipelineId, run.Status);

        AddActivity(BuildActivityTitle(run.Status), $"{run.RepositoryName} GitLab pipeline #{run.GitLabPipelineId} is {run.Status}.", GetActivityTone(run.Status), run.UpdatedUtc == default ? run.StartedUtc : run.UpdatedUtc);
    }

    private void StartPollingForRun(PipelineDefinition pipeline, RepositoryRunContext runContext, WorkflowRunEntity run)
    {
        if (_runPollingTokens.ContainsKey(pipeline.Id))
        {
            return;
        }

        var cancellationTokenSource = new CancellationTokenSource();
        _runPollingTokens[pipeline.Id] = cancellationTokenSource;
        _ = PollGitLabPipelineAsync(pipeline, runContext, run, cancellationTokenSource.Token);
    }

    private async Task PollGitLabPipelineAsync(PipelineDefinition pipeline, RepositoryRunContext runContext, WorkflowRunEntity run, CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var snapshot = await gitLabRepositoryService!.GetPipelineSnapshotAsync(runContext.ProjectReference, run.GitLabPipelineId, runContext.AccessToken, cancellationToken);
                if (!snapshot.Success)
                {
                    AppendConsoleLine(pipeline, snapshot.Message);
                    NotifyStateChanged();
                    await Task.Delay(TimeSpan.FromSeconds(5), cancellationToken);
                    continue;
                }

                pipeline.Status = MapPipelineStatus(snapshot.Status);
                pipeline.Trigger = "GitLab";
                pipeline.LastRun = DateTime.UtcNow;
                pipeline.ExternalPipelineId = snapshot.PipelineId;
                pipeline.ExternalPipelineUrl = snapshot.WebUrl;
                pipeline.Stages = MapJobsToStages(snapshot.Jobs, snapshot.Status);
                pipeline.ConsoleOutput = snapshot.LogLines.Count > 0
                    ? [.. snapshot.LogLines]
                    : ParseLogOutput(run.LogOutput, run.StartedUtc, run.GitLabPipelineId, snapshot.Status);

                UpdateWorkflowRunSnapshot(run.Id, snapshot, pipeline.ConsoleOutput);
                NotifyStateChanged();

                if (snapshot.IsTerminal)
                {
                    AddActivity(BuildActivityTitle(snapshot.Status), $"{pipeline.Name} GitLab pipeline #{snapshot.PipelineId} finished with status {snapshot.Status}.", GetActivityTone(snapshot.Status));
                    break;
                }

                await Task.Delay(TimeSpan.FromSeconds(4), cancellationToken);
            }
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            if (_runPollingTokens.Remove(pipeline.Id, out var cancellationTokenSource))
            {
                cancellationTokenSource.Dispose();
            }

            NotifyStateChanged();
        }
    }

    private WorkflowRunEntity PersistWorkflowRun(WorkflowRunEntity run)
    {
        using var db = dbContextFactory.CreateDbContext();
        db.WorkflowRuns.Add(run);
        db.SaveChanges();
        return run;
    }

    private WorkflowRunEntity? GetLatestActiveRun(string repositoryId)
    {
        using var db = dbContextFactory.CreateDbContext();
        return db.WorkflowRuns
            .AsNoTracking()
            .Where(x => x.RepositoryId == repositoryId && IsActiveRunStatus(x.Status))
            .OrderByDescending(x => x.StartedUtc)
            .FirstOrDefault();
    }

    private void UpdateWorkflowRunStatus(int runId, string status, string logOutput)
    {
        using var db = dbContextFactory.CreateDbContext();
        var existing = db.WorkflowRuns.FirstOrDefault(x => x.Id == runId);
        if (existing is null)
        {
            return;
        }

        existing.Status = status;
        existing.LogOutput = logOutput;
        existing.UpdatedUtc = DateTime.UtcNow;
        if (IsTerminalStatus(status))
        {
            existing.CompletedUtc = DateTime.UtcNow;
        }

        db.SaveChanges();
    }

    private void UpdateWorkflowRunSnapshot(int runId, GitLabPipelineSnapshot snapshot, IReadOnlyList<string> consoleOutput)
    {
        using var db = dbContextFactory.CreateDbContext();
        var existing = db.WorkflowRuns.FirstOrDefault(x => x.Id == runId);
        if (existing is null)
        {
            return;
        }

        existing.Status = snapshot.Status;
        existing.GitLabWebUrl = snapshot.WebUrl;
        existing.GitLabJobIdsJson = JsonSerializer.Serialize(snapshot.Jobs.Select(job => job.JobId).ToList());
        existing.LogOutput = string.Join(Environment.NewLine, consoleOutput.TakeLast(80));
        existing.UpdatedUtc = DateTime.UtcNow;
        if (snapshot.IsTerminal)
        {
            existing.CompletedUtc = DateTime.UtcNow;
        }

        db.SaveChanges();
    }

    private PipelineDefinition GetOrCreatePipeline(string repositoryId, string name, string project)
    {
        var pipeline = _pipelines.FirstOrDefault(p =>
            (!string.IsNullOrWhiteSpace(repositoryId) && p.RepositoryId.Equals(repositoryId, StringComparison.OrdinalIgnoreCase))
            || (p.Name.Equals(name, StringComparison.OrdinalIgnoreCase) && p.Project.Equals(project, StringComparison.OrdinalIgnoreCase)));

        if (pipeline is not null)
        {
            return pipeline;
        }

        pipeline = new PipelineDefinition
        {
            Id = _pipelines.Count == 0 ? 1 : _pipelines.Max(p => p.Id) + 1,
            RepositoryId = repositoryId,
            Name = name,
            Project = project,
            Branch = "main",
            Trigger = "GitLab",
            Status = PipelineStatus.Healthy,
            LastRun = DateTime.UtcNow,
            SuccessRate = 100,
            Stages = CreateQueuedStages(),
            ConsoleOutput = []
        };

        _pipelines.Add(pipeline);
        return pipeline;
    }

    private static bool IsActiveRunStatus(string status)
    {
        return status.Equals("created", StringComparison.OrdinalIgnoreCase)
            || status.Equals("pending", StringComparison.OrdinalIgnoreCase)
            || status.Equals("running", StringComparison.OrdinalIgnoreCase)
            || status.Equals("preparing", StringComparison.OrdinalIgnoreCase)
            || status.Equals("waiting_for_resource", StringComparison.OrdinalIgnoreCase)
            || status.Equals("canceling", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsTerminalStatus(string status)
    {
        return status.Equals("success", StringComparison.OrdinalIgnoreCase)
            || status.Equals("failed", StringComparison.OrdinalIgnoreCase)
            || status.Equals("canceled", StringComparison.OrdinalIgnoreCase)
            || status.Equals("skipped", StringComparison.OrdinalIgnoreCase)
            || status.Equals("manual", StringComparison.OrdinalIgnoreCase);
    }

    private static PipelineStatus MapPipelineStatus(string status)
    {
        return status.ToLowerInvariant() switch
        {
            "success" => PipelineStatus.Healthy,
            "failed" => PipelineStatus.Failed,
            "canceled" => PipelineStatus.Warning,
            "canceling" => PipelineStatus.Warning,
            "skipped" => PipelineStatus.Warning,
            "manual" => PipelineStatus.Warning,
            _ => PipelineStatus.Running
        };
    }

    private static List<PipelineStage> CreateQueuedStages()
    {
        return
        [
            new() { Name = "Checkout", Status = "Queued", Progress = 0, Duration = "--" },
            new() { Name = "Build", Status = "Pending", Progress = 0, Duration = "--" },
            new() { Name = "Tests", Status = "Pending", Progress = 0, Duration = "--" },
            new() { Name = "Deploy", Status = "Pending", Progress = 0, Duration = "--" }
        ];
    }

    private static List<PipelineStage> CreateSuccessfulStages()
    {
        return
        [
            new() { Name = "Checkout", Status = "Succeeded", Progress = 100, Duration = "--" },
            new() { Name = "Build", Status = "Succeeded", Progress = 100, Duration = "--" },
            new() { Name = "Tests", Status = "Succeeded", Progress = 100, Duration = "--" },
            new() { Name = "Deploy", Status = "Succeeded", Progress = 100, Duration = "--" }
        ];
    }

    private static List<PipelineStage> CreateStagesFromRunStatus(string status)
    {
        if (status.Equals("success", StringComparison.OrdinalIgnoreCase))
        {
            return CreateSuccessfulStages();
        }

        return
        [
            new() { Name = "GitLab Pipeline", Status = ToStageStatus(status), Progress = IsTerminalStatus(status) ? 100 : 50, Duration = "--" }
        ];
    }

    private static List<PipelineStage> MapJobsToStages(IReadOnlyList<GitLabPipelineJobSnapshot> jobs, string pipelineStatus)
    {
        if (jobs.Count == 0)
        {
            return CreateStagesFromRunStatus(pipelineStatus);
        }

        return
        [
            .. jobs.Select(job => new PipelineStage
            {
                Name = string.IsNullOrWhiteSpace(job.Stage) ? job.Name : $"{job.Stage}: {job.Name}",
                Status = ToStageStatus(job.Status),
                Progress = MapJobProgress(job.Status),
                Duration = "--"
            })
        ];
    }

    private static int MapJobProgress(string status)
    {
        return status.ToLowerInvariant() switch
        {
            "success" => 100,
            "failed" => 100,
            "canceled" => 100,
            "skipped" => 100,
            "manual" => 100,
            "running" => 60,
            "pending" => 20,
            "created" => 10,
            _ => 0
        };
    }

    private static string ToStageStatus(string status)
    {
        return status.ToLowerInvariant() switch
        {
            "success" => "Succeeded",
            "failed" => "Failed",
            "canceled" => "Canceled",
            "canceling" => "Canceling",
            "running" => "Running",
            "pending" => "Pending",
            "created" => "Queued",
            "manual" => "Manual",
            "skipped" => "Skipped",
            _ => string.IsNullOrWhiteSpace(status) ? "Unknown" : status
        };
    }

    private static List<string> ParseLogOutput(string logOutput, DateTime startedUtc, long pipelineId, string status)
    {
        if (!string.IsNullOrWhiteSpace(logOutput))
        {
            return [.. logOutput.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(line => line.TrimEnd('\r')).TakeLast(80)];
        }

        return
        [
            $"[{startedUtc.ToLocalTime():HH:mm:ss}] GitLab pipeline #{pipelineId} status: {status}"
        ];
    }

    private static void AppendConsoleLine(PipelineDefinition pipeline, string message)
    {
        pipeline.ConsoleOutput.Add($"[{DateTime.Now:HH:mm:ss}] {message}");
        if (pipeline.ConsoleOutput.Count > 80)
        {
            pipeline.ConsoleOutput.RemoveAt(0);
        }
    }

    private static string BuildActivityTitle(string status)
    {
        return status.ToLowerInvariant() switch
        {
            "success" => "Pipeline completed",
            "failed" => "Pipeline failed",
            "canceled" => "Pipeline canceled",
            "canceling" => "Pipeline cancel requested",
            _ => "Pipeline updated"
        };
    }

    private static string GetActivityTone(string status)
    {
        return status.ToLowerInvariant() switch
        {
            "success" => "success",
            "failed" => "danger",
            "canceled" => "warning",
            "canceling" => "warning",
            _ => "neutral"
        };
    }

    private void AddActivity(string title, string detail, string tone, DateTime? timestamp = null)
    {
        _activities.Insert(0, new PipelineActivity
        {
            Title = title,
            Detail = detail,
            Tone = tone,
            Timestamp = timestamp ?? DateTime.UtcNow
        });

        if (_activities.Count > 20)
        {
            _activities.RemoveRange(20, _activities.Count - 20);
        }
    }

    private void NotifyStateChanged() => Changed?.Invoke();
}
