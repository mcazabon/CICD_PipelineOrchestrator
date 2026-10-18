using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CICD_PipelineOrchestrator.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddAgentAuthAndInvocations : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "AgentTokenEncrypted",
                table: "RepositorySources",
                type: "nvarchar(4000)",
                maxLength: 4000,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "AuthScheme",
                table: "RepositorySources",
                type: "nvarchar(50)",
                maxLength: 50,
                nullable: false,
                defaultValue: "Auto");

            migrationBuilder.CreateTable(
                name: "AgentInvocations",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    SourceConfigurationId = table.Column<string>(type: "nvarchar(300)", maxLength: 300, nullable: false),
                    ProjectReference = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    InvokedBy = table.Column<string>(type: "nvarchar(250)", maxLength: 250, nullable: false),
                    InvokedUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    WorkflowId = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    Success = table.Column<bool>(type: "bit", nullable: false),
                    Message = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: false),
                    AuthScheme = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AgentInvocations", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_AgentInvocations_InvokedUtc",
                table: "AgentInvocations",
                column: "InvokedUtc");

            migrationBuilder.CreateIndex(
                name: "IX_AgentInvocations_SourceConfigurationId",
                table: "AgentInvocations",
                column: "SourceConfigurationId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "AgentInvocations");

            migrationBuilder.DropColumn(
                name: "AgentTokenEncrypted",
                table: "RepositorySources");

            migrationBuilder.DropColumn(
                name: "AuthScheme",
                table: "RepositorySources");
        }
    }
}
