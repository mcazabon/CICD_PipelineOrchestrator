using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CICD_PipelineOrchestrator.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddWorkflowRunTracking : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "WorkflowRuns",
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
                    Status = table.Column<string>(type: "nvarchar(80)", maxLength: 80, nullable: false),
                    GitLabPipelineId = table.Column<long>(type: "bigint", nullable: false),
                    GitLabWebUrl = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: false),
                    GitLabJobIdsJson = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    LogOutput = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    StartedUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    UpdatedUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    CompletedUtc = table.Column<DateTime>(type: "datetime2", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_WorkflowRuns", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_WorkflowRuns_GitLabPipelineId",
                table: "WorkflowRuns",
                column: "GitLabPipelineId");

            migrationBuilder.CreateIndex(
                name: "IX_WorkflowRuns_SourceConfigurationId_RepositoryId",
                table: "WorkflowRuns",
                columns: new[] { "SourceConfigurationId", "RepositoryId" });

            migrationBuilder.CreateIndex(
                name: "IX_WorkflowRuns_StartedUtc",
                table: "WorkflowRuns",
                column: "StartedUtc");

            migrationBuilder.CreateIndex(
                name: "IX_WorkflowRuns_Status",
                table: "WorkflowRuns",
                column: "Status");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "WorkflowRuns");
        }
    }
}
