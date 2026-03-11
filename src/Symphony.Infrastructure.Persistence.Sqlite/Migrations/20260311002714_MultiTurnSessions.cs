using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Symphony.Infrastructure.Persistence.Sqlite.Migrations
{
    /// <inheritdoc />
    public partial class MultiTurnSessions : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_sessions_RunAttemptId",
                table: "sessions");

            migrationBuilder.CreateIndex(
                name: "IX_sessions_RunAttemptId",
                table: "sessions",
                column: "RunAttemptId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_sessions_RunAttemptId",
                table: "sessions");

            migrationBuilder.CreateIndex(
                name: "IX_sessions_RunAttemptId",
                table: "sessions",
                column: "RunAttemptId",
                unique: true);
        }
    }
}
