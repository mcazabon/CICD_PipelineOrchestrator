USE [master]
GO

IF DB_ID(N'CICD_Orchestrator') IS NULL
BEGIN
	CREATE DATABASE [CICD_Orchestrator];
END
GO

USE [CICD_Orchestrator]
GO

IF SCHEMA_ID(N'dbo') IS NULL
BEGIN
	EXEC(N'CREATE SCHEMA [dbo]');
END
GO

IF OBJECT_ID(N'[dbo].[RepositorySources]', N'U') IS NULL
BEGIN
	CREATE TABLE [dbo].[RepositorySources]
	(
		[SourceConfigurationId] NVARCHAR(300) NOT NULL,
		[Provider] INT NOT NULL,
		[RepositoryUrl] NVARCHAR(500) NOT NULL,
		[AccessTokenEncrypted] NVARCHAR(4000) NOT NULL,
		[CreatedUtc] DATETIME2 NOT NULL,
		[UpdatedUtc] DATETIME2 NOT NULL,
		CONSTRAINT [PK_RepositorySources] PRIMARY KEY ([SourceConfigurationId])
	);
END
GO

IF OBJECT_ID(N'[dbo].[RepositoryProjects]', N'U') IS NULL
BEGIN
	CREATE TABLE [dbo].[RepositoryProjects]
	(
		[Id] INT IDENTITY(1,1) NOT NULL,
		[SourceConfigurationId] NVARCHAR(300) NOT NULL,
		[RepositoryId] NVARCHAR(300) NOT NULL,
		[Name] NVARCHAR(250) NOT NULL,
		[Provider] INT NOT NULL,
		[Owner] NVARCHAR(250) NOT NULL,
		[DefaultBranch] NVARCHAR(120) NOT NULL,
		[Path] NVARCHAR(500) NOT NULL,
		[DetectedType] INT NOT NULL,
		[Confidence] INT NOT NULL,
		[DetectionSummary] NVARCHAR(MAX) NOT NULL,
		[LastUpdated] NVARCHAR(64) NOT NULL,
		[DeploymentTarget] NVARCHAR(120) NOT NULL,
		[RequirePeerReview] BIT NOT NULL,
		[RequireChangeTicket] BIT NOT NULL,
		[LiveFetchAvailable] BIT NOT NULL,
		[LiveFetchStatus] NVARCHAR(1000) NOT NULL,
		[SourceDisplayName] NVARCHAR(250) NOT NULL,
		[SourceRepositoryUrl] NVARCHAR(500) NOT NULL,
		[FingerprintJson] NVARCHAR(MAX) NOT NULL,
		CONSTRAINT [PK_RepositoryProjects] PRIMARY KEY ([Id])
	);
END
GO

IF NOT EXISTS
(
	SELECT 1
	FROM sys.indexes
	WHERE name = N'IX_RepositoryProjects_SourceConfigurationId_RepositoryId'
	  AND object_id = OBJECT_ID(N'[dbo].[RepositoryProjects]')
)
BEGIN
	CREATE UNIQUE INDEX [IX_RepositoryProjects_SourceConfigurationId_RepositoryId]
		ON [dbo].[RepositoryProjects] ([SourceConfigurationId], [RepositoryId]);
END
GO

IF OBJECT_ID(N'[dbo].[WorkflowDeployments]', N'U') IS NULL
BEGIN
	CREATE TABLE [dbo].[WorkflowDeployments]
	(
		[Id] INT IDENTITY(1,1) NOT NULL,
		[SourceConfigurationId] NVARCHAR(300) NOT NULL,
		[RepositoryId] NVARCHAR(300) NOT NULL,
		[RepositoryName] NVARCHAR(250) NOT NULL,
		[ProjectOwner] NVARCHAR(250) NOT NULL,
		[Provider] INT NOT NULL,
		[Branch] NVARCHAR(120) NOT NULL,
		[WorkflowPath] NVARCHAR(500) NOT NULL,
		[TemplateId] NVARCHAR(120) NOT NULL,
		[YamlContent] NVARCHAR(MAX) NOT NULL,
		[DeployedUtc] DATETIME2 NOT NULL,
		CONSTRAINT [PK_WorkflowDeployments] PRIMARY KEY ([Id])
	);
END
GO

IF NOT EXISTS
(
	SELECT 1
	FROM sys.indexes
	WHERE name = N'IX_WorkflowDeployments_DeployedUtc'
	  AND object_id = OBJECT_ID(N'[dbo].[WorkflowDeployments]')
)
BEGIN
	CREATE INDEX [IX_WorkflowDeployments_DeployedUtc]
		ON [dbo].[WorkflowDeployments] ([DeployedUtc]);
END
GO

IF NOT EXISTS
(
	SELECT 1
	FROM sys.indexes
	WHERE name = N'IX_WorkflowDeployments_SourceConfigurationId_RepositoryId'
	  AND object_id = OBJECT_ID(N'[dbo].[WorkflowDeployments]')
)
BEGIN
	CREATE INDEX [IX_WorkflowDeployments_SourceConfigurationId_RepositoryId]
		ON [dbo].[WorkflowDeployments] ([SourceConfigurationId], [RepositoryId]);
END
GO

IF OBJECT_ID(N'[dbo].[WorkflowRuns]', N'U') IS NULL
BEGIN
	CREATE TABLE [dbo].[WorkflowRuns]
	(
		[Id] INT IDENTITY(1,1) NOT NULL,
		[SourceConfigurationId] NVARCHAR(300) NOT NULL,
		[RepositoryId] NVARCHAR(300) NOT NULL,
		[RepositoryName] NVARCHAR(250) NOT NULL,
		[ProjectOwner] NVARCHAR(250) NOT NULL,
		[Provider] INT NOT NULL,
		[Branch] NVARCHAR(120) NOT NULL,
		[Status] NVARCHAR(80) NOT NULL,
		[GitLabPipelineId] BIGINT NOT NULL,
		[GitLabWebUrl] NVARCHAR(1000) NOT NULL,
		[GitLabJobIdsJson] NVARCHAR(MAX) NOT NULL,
		[LogOutput] NVARCHAR(MAX) NOT NULL,
		[StartedUtc] DATETIME2 NOT NULL,
		[UpdatedUtc] DATETIME2 NOT NULL,
		[CompletedUtc] DATETIME2 NULL,
		CONSTRAINT [PK_WorkflowRuns] PRIMARY KEY ([Id])
	);
END
GO

IF NOT EXISTS
(
	SELECT 1
	FROM sys.indexes
	WHERE name = N'IX_WorkflowRuns_SourceConfigurationId_RepositoryId'
	  AND object_id = OBJECT_ID(N'[dbo].[WorkflowRuns]')
)
BEGIN
	CREATE INDEX [IX_WorkflowRuns_SourceConfigurationId_RepositoryId]
		ON [dbo].[WorkflowRuns] ([SourceConfigurationId], [RepositoryId]);
END
GO

IF NOT EXISTS
(
	SELECT 1
	FROM sys.indexes
	WHERE name = N'IX_WorkflowRuns_GitLabPipelineId'
	  AND object_id = OBJECT_ID(N'[dbo].[WorkflowRuns]')
)
BEGIN
	CREATE INDEX [IX_WorkflowRuns_GitLabPipelineId]
		ON [dbo].[WorkflowRuns] ([GitLabPipelineId]);
END
GO

IF NOT EXISTS
(
	SELECT 1
	FROM sys.indexes
	WHERE name = N'IX_WorkflowRuns_Status'
	  AND object_id = OBJECT_ID(N'[dbo].[WorkflowRuns]')
)
BEGIN
	CREATE INDEX [IX_WorkflowRuns_Status]
		ON [dbo].[WorkflowRuns] ([Status]);
END
GO

IF NOT EXISTS
(
	SELECT 1
	FROM sys.indexes
	WHERE name = N'IX_WorkflowRuns_StartedUtc'
	  AND object_id = OBJECT_ID(N'[dbo].[WorkflowRuns]')
)
BEGIN
	CREATE INDEX [IX_WorkflowRuns_StartedUtc]
		ON [dbo].[WorkflowRuns] ([StartedUtc]);
END
GO
