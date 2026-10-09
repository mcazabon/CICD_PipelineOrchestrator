using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CICD_PipelineOrchestrator.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddWorkflowDeploymentHistory : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "WorkflowDeployments",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    SourceConfigurationId = table.Column<string>(type: "nvarchar(300)", maxLength: 300, nullable: false),
                    RepositoryId = table.Column<string>(type: "nvarchar(300)", maxLength: 300, nullable: false),
                    RepositoryName = table.Column<string>(type: "nvarchar(250)", maxLength: 250, nullable: false),
                    ProjectOwner = table.Column<string>(type: "nvarchar(250)", maxLength: 250, nullable: false),
                    Provider = table.Column<int>(type: "int", nullable: false),
                    Branch = table.Column<string>(type: "nvarchar(120)", maxLength: 120, nullable: false),
                    WorkflowPath = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: false),
                    TemplateId = table.Column<string>(type: "nvarchar(120)", maxLength: 120, nullable: false),
                    YamlContent = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    DeployedUtc = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_WorkflowDeployments", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_WorkflowDeployments_DeployedUtc",
                table: "WorkflowDeployments",
                column: "DeployedUtc");

            migrationBuilder.CreateIndex(
                name: "IX_WorkflowDeployments_SourceConfigurationId_RepositoryId",
                table: "WorkflowDeployments",
                columns: new[] { "SourceConfigurationId", "RepositoryId" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "WorkflowDeployments");
        }
    }
}
