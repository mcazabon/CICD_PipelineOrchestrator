using CICD_PipelineOrchestrator.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace CICD_PipelineOrchestrator.Data;

public sealed class RepositoryCatalogDbContext(DbContextOptions<RepositoryCatalogDbContext> options) : DbContext(options)
{
    public DbSet<RepositorySourceEntity> RepositorySources => Set<RepositorySourceEntity>();
    public DbSet<RepositoryProjectEntity> RepositoryProjects => Set<RepositoryProjectEntity>();
    public DbSet<WorkflowDeploymentEntity> WorkflowDeployments => Set<WorkflowDeploymentEntity>();
    public DbSet<WorkflowRunEntity> WorkflowRuns => Set<WorkflowRunEntity>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        modelBuilder.Entity<RepositorySourceEntity>(entity =>
        {
            entity.ToTable("RepositorySources");
            entity.HasKey(x => x.SourceConfigurationId);
            entity.Property(x => x.SourceConfigurationId).HasMaxLength(300);
            entity.Property(x => x.RepositoryUrl).HasMaxLength(500);
            entity.Property(x => x.AccessTokenEncrypted).HasMaxLength(4000);
        });

        modelBuilder.Entity<RepositoryProjectEntity>(entity =>
        {
            entity.ToTable("RepositoryProjects");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.SourceConfigurationId).HasMaxLength(300);
            entity.Property(x => x.RepositoryId).HasMaxLength(300);
            entity.Property(x => x.Name).HasMaxLength(250);
            entity.Property(x => x.Owner).HasMaxLength(250);
            entity.Property(x => x.DefaultBranch).HasMaxLength(120);
            entity.Property(x => x.Path).HasMaxLength(500);
            entity.Property(x => x.LastUpdated).HasMaxLength(64);
            entity.Property(x => x.DeploymentTarget).HasMaxLength(120);
            entity.Property(x => x.LiveFetchStatus).HasMaxLength(1000);
            entity.Property(x => x.SourceDisplayName).HasMaxLength(250);
            entity.Property(x => x.SourceRepositoryUrl).HasMaxLength(500);
            entity.Property(x => x.FingerprintJson).HasMaxLength(12000);

            entity.HasIndex(x => new { x.SourceConfigurationId, x.RepositoryId }).IsUnique();
        });

        modelBuilder.Entity<WorkflowDeploymentEntity>(entity =>
        {
            entity.ToTable("WorkflowDeployments");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.SourceConfigurationId).HasMaxLength(300);
            entity.Property(x => x.RepositoryId).HasMaxLength(300);
            entity.Property(x => x.RepositoryName).HasMaxLength(250);
            entity.Property(x => x.ProjectOwner).HasMaxLength(250);
            entity.Property(x => x.Branch).HasMaxLength(120);
            entity.Property(x => x.WorkflowPath).HasMaxLength(500);
            entity.Property(x => x.TemplateId).HasMaxLength(120);

            entity.HasIndex(x => x.DeployedUtc);
            entity.HasIndex(x => new { x.SourceConfigurationId, x.RepositoryId });
        });

        modelBuilder.Entity<WorkflowRunEntity>(entity =>
        {
            entity.ToTable("WorkflowRuns");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.SourceConfigurationId).HasMaxLength(300);
            entity.Property(x => x.RepositoryId).HasMaxLength(300);
            entity.Property(x => x.RepositoryName).HasMaxLength(250);
            entity.Property(x => x.ProjectOwner).HasMaxLength(250);
            entity.Property(x => x.Branch).HasMaxLength(120);
            entity.Property(x => x.Status).HasMaxLength(80);
            entity.Property(x => x.GitLabWebUrl).HasMaxLength(1000);

            entity.HasIndex(x => new { x.SourceConfigurationId, x.RepositoryId });
            entity.HasIndex(x => x.GitLabPipelineId);
            entity.HasIndex(x => x.Status);
            entity.HasIndex(x => x.StartedUtc);
        });
    }
}
