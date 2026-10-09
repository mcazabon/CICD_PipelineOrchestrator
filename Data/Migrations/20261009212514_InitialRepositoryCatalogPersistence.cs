using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CICD_PipelineOrchestrator.Data.Migrations
{
    /// <inheritdoc />
    public partial class InitialRepositoryCatalogPersistence : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "RepositoryProjects",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    SourceConfigurationId = table.Column<string>(type: "nvarchar(300)", maxLength: 300, nullable: false),
                    RepositoryId = table.Column<string>(type: "nvarchar(300)", maxLength: 300, nullable: false),
                    Name = table.Column<string>(type: "nvarchar(250)", maxLength: 250, nullable: false),
                    Provider = table.Column<int>(type: "int", nullable: false),
                    Owner = table.Column<string>(type: "nvarchar(250)", maxLength: 250, nullable: false),
                    DefaultBranch = table.Column<string>(type: "nvarchar(120)", maxLength: 120, nullable: false),
                    Path = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: false),
                    DetectedType = table.Column<int>(type: "int", nullable: false),
                    Confidence = table.Column<int>(type: "int", nullable: false),
                    DetectionSummary = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    LastUpdated = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    DeploymentTarget = table.Column<string>(type: "nvarchar(120)", maxLength: 120, nullable: false),
                    RequirePeerReview = table.Column<bool>(type: "bit", nullable: false),
                    RequireChangeTicket = table.Column<bool>(type: "bit", nullable: false),
                    LiveFetchAvailable = table.Column<bool>(type: "bit", nullable: false),
                    LiveFetchStatus = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: false),
                    SourceDisplayName = table.Column<string>(type: "nvarchar(250)", maxLength: 250, nullable: false),
                    SourceRepositoryUrl = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: false),
                    FingerprintJson = table.Column<string>(type: "nvarchar(max)", maxLength: 12000, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RepositoryProjects", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "RepositorySources",
                columns: table => new
                {
                    SourceConfigurationId = table.Column<string>(type: "nvarchar(300)", maxLength: 300, nullable: false),
                    Provider = table.Column<int>(type: "int", nullable: false),
                    RepositoryUrl = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: false),
                    AccessTokenEncrypted = table.Column<string>(type: "nvarchar(4000)", maxLength: 4000, nullable: false),
                    CreatedUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    UpdatedUtc = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RepositorySources", x => x.SourceConfigurationId);
                });

            migrationBuilder.CreateIndex(
                name: "IX_RepositoryProjects_SourceConfigurationId_RepositoryId",
                table: "RepositoryProjects",
                columns: new[] { "SourceConfigurationId", "RepositoryId" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "RepositoryProjects");

            migrationBuilder.DropTable(
                name: "RepositorySources");
        }
    }
}
