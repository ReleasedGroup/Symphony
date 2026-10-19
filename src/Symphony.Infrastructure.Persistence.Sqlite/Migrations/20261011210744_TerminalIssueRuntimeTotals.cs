using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Symphony.Infrastructure.Persistence.Sqlite.Migrations
{
    /// <inheritdoc />
    public partial class TerminalIssueRuntimeTotals : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(name: "IX_dispatch_claims_IssueId", table: "dispatch_claims");
            migrationBuilder.CreateIndex(name: "IX_dispatch_claims_IssueId", table: "dispatch_claims",
                column: "IssueId", unique: true, filter: "Status IN ('active', 'cleanup')");

            migrationBuilder.CreateTable(
                name: "runtime_totals",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    InputTokens = table.Column<long>(type: "INTEGER", nullable: false),
                    OutputTokens = table.Column<long>(type: "INTEGER", nullable: false),
                    TotalTokens = table.Column<long>(type: "INTEGER", nullable: false),
                    SecondsRunning = table.Column<double>(type: "REAL", nullable: false),
                    RateLimitsEventId = table.Column<long>(type: "INTEGER", nullable: false),
                    RateLimitsJson = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_runtime_totals", x => x.Id);
                });

            migrationBuilder.InsertData(
                table: "runtime_totals",
                columns: new[] { "Id", "InputTokens", "OutputTokens", "RateLimitsEventId", "RateLimitsJson", "SecondsRunning", "TotalTokens" },
                values: new object[] { 1, 0L, 0L, 0L, null, 0.0, 0L });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(name: "IX_dispatch_claims_IssueId", table: "dispatch_claims");
            migrationBuilder.CreateIndex(name: "IX_dispatch_claims_IssueId", table: "dispatch_claims",
                column: "IssueId", unique: true, filter: "Status = 'active'");

            migrationBuilder.DropTable(
                name: "runtime_totals");
        }
    }
}
