using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Symphony.Infrastructure.Persistence.Sqlite.Migrations
{
    /// <inheritdoc />
    public partial class InitialCreate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "dispatch_claims",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    IssueId = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    IssueIdentifier = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    ClaimedByInstanceId = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    ClaimedAtUtc = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    ReleasedAtUtc = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                    Status = table.Column<string>(type: "TEXT", maxLength: 50, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_dispatch_claims", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "instance_leases",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    LeaseName = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    OwnerInstanceId = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    AcquiredAtUtc = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    ExpiresAtUtc = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    UpdatedAtUtc = table.Column<DateTimeOffset>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_instance_leases", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_dispatch_claims_IssueId",
                table: "dispatch_claims",
                column: "IssueId");

            migrationBuilder.CreateIndex(
                name: "IX_dispatch_claims_IssueId_Status",
                table: "dispatch_claims",
                columns: new[] { "IssueId", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_dispatch_claims_Status",
                table: "dispatch_claims",
                column: "Status");

            migrationBuilder.CreateIndex(
                name: "IX_instance_leases_LeaseName",
                table: "instance_leases",
                column: "LeaseName",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "dispatch_claims");

            migrationBuilder.DropTable(
                name: "instance_leases");
        }
    }
}
