namespace CICD_PipelineOrchestrator.Models;

public enum DetectedProjectType
{
    AspNetCoreApi,
    DotNetWorker,
    ReactWeb,
    AngularApp,
    JavaMaven,
    PythonService,
    SqlDatabase,
    Unknown
}

public enum RepositoryProvider
{
    GitHub,
    GitLab,
    AzureDevOps
}

public sealed class RepositoryDefinition
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public RepositoryProvider Provider { get; set; } = RepositoryProvider.GitHub;
    public string Owner { get; set; } = "contoso";
    public string DefaultBranch { get; set; } = "develop";
    public string Path { get; set; } = string.Empty;
    public DetectedProjectType DetectedType { get; set; } = DetectedProjectType.Unknown;
    public int Confidence { get; set; } = 0;
    public string DetectionSummary { get; set; } = string.Empty;
    public ProjectFingerprint Fingerprint { get; set; } = new();
    public string LastUpdated { get; set; } = "2026-10-09";
    public string DeploymentTarget { get; set; } = "DEV-WEB-01";
    public bool RequirePeerReview { get; set; } = true;
    public bool RequireChangeTicket { get; set; } = true;
    public bool LiveFetchAvailable { get; set; } = true;
    public string LiveFetchStatus { get; set; } = "Live fetch available";
    public string SourceConfigurationId { get; set; } = string.Empty;
    public string SourceDisplayName { get; set; } = string.Empty;
    public string SourceRepositoryUrl { get; set; } = string.Empty;
}

public sealed class ProjectFingerprint
{
    public DetectedProjectType Type { get; set; } = DetectedProjectType.Unknown;
    public int Score { get; set; } = 0;
    public string Summary { get; set; } = string.Empty;
    public IReadOnlyList<ProjectFingerprintSignal> Signals { get; set; } = [];
}

public sealed class ProjectFingerprintSignal
{
    public string Name { get; set; } = string.Empty;
    public string Evidence { get; set; } = string.Empty;
    public int Weight { get; set; }
    public bool Matched { get; set; }
}

public sealed class PipelineTemplateDefinition
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string WorkflowFile { get; set; } = string.Empty;
    public string Trigger { get; set; } = "push";
    public string Description { get; set; } = string.Empty;
    public string TemplateText { get; set; } = string.Empty;
}

public sealed class ReleaseWorkflowRequest
{
    public string RepositoryId { get; set; } = string.Empty;
    public string RepositoryName { get; set; } = string.Empty;
    public string SelectedTemplate { get; set; } = string.Empty;
    public string Environment { get; set; } = "DEV";
    public string ArtifactVersion { get; set; } = "v1.0.0";
    public bool UatValidated { get; set; } = true;
    public string ChangeTicket { get; set; } = string.Empty;
    public string SourceBranch { get; set; } = "develop";
    public bool RequirePeerReview { get; set; } = true;
}
