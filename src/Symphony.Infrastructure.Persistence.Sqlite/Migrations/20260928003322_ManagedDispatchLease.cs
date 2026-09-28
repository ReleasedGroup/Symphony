using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Symphony.Infrastructure.Persistence.Sqlite.Migrations
{
    /// <inheritdoc />
    public partial class ManagedDispatchLease : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "managed_lease",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    InstanceId = table.Column<string>(type: "TEXT", maxLength: 200, nullable: true),
                    GenerationId = table.Column<string>(type: "TEXT", maxLength: 200, nullable: true),
                    CurrentEpoch = table.Column<long>(type: "INTEGER", nullable: false),
                    ExpiresAtUtc = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                    UpdatedAtUtc = table.Column<DateTimeOffset>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_managed_lease", x => x.Id);
                });

            migrationBuilder.InsertData(
                table: "managed_lease",
                columns: new[] { "Id", "CurrentEpoch", "ExpiresAtUtc", "GenerationId", "InstanceId", "UpdatedAtUtc" },
                values: new object[] { 1, 0L, null, null, null, new DateTimeOffset(new DateTime(1970, 1, 1, 0, 0, 0, 0, DateTimeKind.Unspecified), new TimeSpan(0, 0, 0, 0, 0)) });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "managed_lease");
        }
    }
}
