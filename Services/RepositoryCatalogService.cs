using CICD_PipelineOrchestrator.Models;

namespace CICD_PipelineOrchestrator.Services;

public sealed class RepositoryCatalogService
{
    private readonly GitLabRepositoryService? _gitLabRepositoryService;

    public RepositoryCatalogService(GitLabRepositoryService? gitLabRepositoryService = null)
    {
        _gitLabRepositoryService = gitLabRepositoryService;
    }

    public IReadOnlyList<RepositoryDefinition> GetRepositories()
    {
        var gitLabProjects = _gitLabRepositoryService is not null
            ? _gitLabRepositoryService.GetProjectsAsync().GetAwaiter().GetResult()
            : Array.Empty<RepositoryDefinition>();

        if (gitLabProjects.Count > 0)
        {
            return gitLabProjects;
        }

        return new List<RepositoryDefinition>
        {
            new()
            {
                Id = "gitlab-customer-api",
                Name = "customer-api",
                Provider = RepositoryProvider.GitLab,
                Owner = "engineering-platform",
                DefaultBranch = "develop",
                Path = "src/CustomerApi/CustomerApi.csproj",
                DetectedType = DetectedProjectType.AspNetCoreApi,
                Confidence = 97,
                DetectionSummary = "GitLab repository detected as ASP.NET Core Web API via .csproj and Microsoft.NET.Sdk.Web.",
                LastUpdated = "2026-10-09",
                DeploymentTarget = "DEV-WEB-01",
                RequirePeerReview = true,
                RequireChangeTicket = true
            },
            new()
            {
                Id = "gitlab-portal-web",
                Name = "portal-web",
                Provider = RepositoryProvider.GitLab,
                Owner = "digital-experience",
                DefaultBranch = "main",
                Path = "src/Portal/package.json",
                DetectedType = DetectedProjectType.ReactWeb,
                Confidence = 93,
                DetectionSummary = "GitLab repository detected as React/Vite project using package.json and frontend build scripts.",
                LastUpdated = "2026-10-09",
                DeploymentTarget = "DEV-WEB-01",
                RequirePeerReview = true,
                RequireChangeTicket = false
            },
            new()
            {
                Id = "github-payroll-worker",
                Name = "PayrollWorker",
                Provider = RepositoryProvider.GitHub,
                Owner = "contoso-platform",
                DefaultBranch = "main",
                Path = "src/PayrollWorker/PayrollWorker.csproj",
                DetectedType = DetectedProjectType.DotNetWorker,
                Confidence = 95,
                DetectionSummary = "GitHub repository detected as .NET worker service via background service model.",
                LastUpdated = "2026-10-08",
                DeploymentTarget = "DEV-BG-02",
                RequirePeerReview = true,
                RequireChangeTicket = true
            },
            new()
            {
                Id = "gitlab-data-pipeline",
                Name = "data-pipeline",
                Provider = RepositoryProvider.GitLab,
                Owner = "analytics-platform",
                DefaultBranch = "release",
                Path = "database/Enterprise.Data.sqlproj",
                DetectedType = DetectedProjectType.SqlDatabase,
                Confidence = 89,
                DetectionSummary = "GitLab repository detected as SQL Server database project using a DACPAC build structure.",
                LastUpdated = "2026-10-08",
                DeploymentTarget = "DEV-DB-01",
                RequirePeerReview = true,
                RequireChangeTicket = true
            }
        };
    }

    public RepositoryDefinition AnalyzeRepository(string repositoryId)
    {
        var repository = GetRepositories().FirstOrDefault(r => r.Id == repositoryId);
        return repository ?? new RepositoryDefinition
        {
            Id = repositoryId,
            Name = repositoryId,
            Path = "./",
            DetectedType = DetectedProjectType.Unknown,
            Confidence = 0,
            DetectionSummary = "Repository not yet analyzed. Manual review required."
        };
    }

    public IReadOnlyList<PipelineTemplateDefinition> GetTemplateCatalog()
    {
        return new List<PipelineTemplateDefinition>
        {
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
                Id = "gitlab-react-web",
                Name = "GitLab React Web",
                WorkflowFile = ".gitlab-ci.yml",
                Trigger = "push",
                Description = "GitLab CI pipeline for a React frontend build and deployment artifact promotion.",
                TemplateText = GetGitLabReactTemplate()
            }
        };
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
                DetectedProjectType.DotNetWorker => catalog.First(t => t.Id == "dotnet-worker"),
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
            _ => catalog.First()
        };
    }

    public string GeneratePreview(RepositoryDefinition repository)
    {
        var template = GetTemplateForRepository(repository);
        var environment = repository.RequirePeerReview ? "uat" : "dev";
        return $"{template.TemplateText}\n\n# Generated for {repository.Name}\n# Source provider: {repository.Provider}\n# Detected project: {repository.DetectedType}\n# Deployment target: {repository.DeploymentTarget}\n# Promotion path: dev -> {environment} -> production";
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

