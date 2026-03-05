using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Symphony.Infrastructure.Persistence.Sqlite.Migrations
{
    /// <inheritdoc />
    public partial class LeaseClaimUniqueActive : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_dispatch_claims_IssueId",
                table: "dispatch_claims");

            migrationBuilder.CreateIndex(
                name: "IX_dispatch_claims_IssueId",
                table: "dispatch_claims",
                column: "IssueId",
                unique: true,
                filter: "Status = 'active'");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_dispatch_claims_IssueId",
                table: "dispatch_claims");

            migrationBuilder.CreateIndex(
                name: "IX_dispatch_claims_IssueId",
                table: "dispatch_claims",
                column: "IssueId");
        }
    }
}
