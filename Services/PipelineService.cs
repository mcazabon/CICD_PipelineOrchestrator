using CICD_PipelineOrchestrator.Models;

namespace CICD_PipelineOrchestrator.Services;

public sealed class PipelineService
{
    private readonly List<PipelineDefinition> _pipelines =
    [
        new PipelineDefinition
        {
            Id = 1,
            Name = "web-frontend",
            Project = "Customer Portal",
            Branch = "main",
            Trigger = "Push",
            Status = PipelineStatus.Running,
            LastRun = DateTime.UtcNow.AddMinutes(-18),
            SuccessRate = 96,
            Stages =
            [
                new() { Name = "Checkout", Status = "Succeeded", Progress = 100, Duration = "00:01:52" },
                new() { Name = "Build", Status = "Running", Progress = 72, Duration = "00:04:31" },
                new() { Name = "Tests", Status = "Pending", Progress = 0, Duration = "--" },
                new() { Name = "Deploy", Status = "Pending", Progress = 0, Duration = "--" }
            ]
        },
        new PipelineDefinition
        {
            Id = 2,
            Name = "commerce-api",
            Project = "Orders API",
            Branch = "release/2026.10",
            Trigger = "Scheduled",
            Status = PipelineStatus.Healthy,
            LastRun = DateTime.UtcNow.AddHours(-2),
            SuccessRate = 98,
            Stages =
            [
                new() { Name = "Checkout", Status = "Succeeded", Progress = 100, Duration = "00:01:12" },
                new() { Name = "Build", Status = "Succeeded", Progress = 100, Duration = "00:02:48" },
                new() { Name = "Tests", Status = "Succeeded", Progress = 100, Duration = "00:03:54" },
                new() { Name = "Deploy", Status = "Succeeded", Progress = 100, Duration = "00:02:11" }
            ]
        },
        new PipelineDefinition
        {
            Id = 3,
            Name = "data-sync",
            Project = "Analytics",
            Branch = "develop",
            Trigger = "Manual",
            Status = PipelineStatus.Warning,
            LastRun = DateTime.UtcNow.AddHours(-4),
            SuccessRate = 88,
            Stages =
            [
                new() { Name = "Checkout", Status = "Succeeded", Progress = 100, Duration = "00:00:54" },
                new() { Name = "Extract", Status = "Warning", Progress = 62, Duration = "00:07:39" },
                new() { Name = "Transform", Status = "Pending", Progress = 0, Duration = "--" },
                new() { Name = "Load", Status = "Pending", Progress = 0, Duration = "--" }
            ]
        }
    ];

    public IReadOnlyList<PipelineDefinition> GetPipelines() => _pipelines.AsReadOnly();

    public PipelineSummary GetSummary()
    {
        var totalPipelines = _pipelines.Count;
        var activeRuns = _pipelines.Count(p => p.Status == PipelineStatus.Running);
        var deploymentsToday = _pipelines.Count(p => p.LastRun.Date == DateTime.UtcNow.Date);
        var successRate = (int)Math.Round(_pipelines.Average(p => p.SuccessRate));

        return new PipelineSummary
        {
            TotalPipelines = totalPipelines,
            ActiveRuns = activeRuns,
            DeploymentsToday = deploymentsToday,
            SuccessRate = successRate
        };
    }

    public IReadOnlyList<PipelineActivity> GetRecentActivity()
    {
        return new List<PipelineActivity>
        {
            new() { Title = "Build completed", Detail = "commerce-api deployed to production", Timestamp = DateTime.UtcNow.AddMinutes(-45), Tone = "success" },
            new() { Title = "Approval required", Detail = "web-frontend awaiting release signoff", Timestamp = DateTime.UtcNow.AddMinutes(-90), Tone = "warning" },
            new() { Title = "Pipeline started", Detail = "data-sync triggered by manual queue", Timestamp = DateTime.UtcNow.AddHours(-3), Tone = "neutral" },
            new() { Title = "Failure detected", Detail = "analytics ingestion missed SLA threshold", Timestamp = DateTime.UtcNow.AddHours(-6), Tone = "danger" }
        };
    }

    public void TogglePipelineStatus(int id)
    {
        var pipeline = _pipelines.FirstOrDefault(p => p.Id == id);
        if (pipeline is null)
        {
            return;
        }

        pipeline.Status = pipeline.Status == PipelineStatus.Running
            ? PipelineStatus.Healthy
            : PipelineStatus.Running;

        pipeline.LastRun = DateTime.UtcNow;
    }

    public void AddPipeline(string name, string project, string branch, string trigger)
    {
        if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(project))
        {
            return;
        }

        var newPipeline = new PipelineDefinition
        {
            Id = _pipelines.Count == 0 ? 1 : _pipelines.Max(p => p.Id) + 1,
            Name = name.Trim(),
            Project = project.Trim(),
            Branch = string.IsNullOrWhiteSpace(branch) ? "main" : branch.Trim(),
            Trigger = string.IsNullOrWhiteSpace(trigger) ? "Manual" : trigger.Trim(),
            Status = PipelineStatus.Healthy,
            LastRun = DateTime.UtcNow,
            SuccessRate = 100,
            Stages =
            [
                new() { Name = "Checkout", Status = "Queued", Progress = 0, Duration = "--" },
                new() { Name = "Build", Status = "Pending", Progress = 0, Duration = "--" },
                new() { Name = "Tests", Status = "Pending", Progress = 0, Duration = "--" },
                new() { Name = "Deploy", Status = "Pending", Progress = 0, Duration = "--" }
            ]
        };

        _pipelines.Add(newPipeline);
    }
}
