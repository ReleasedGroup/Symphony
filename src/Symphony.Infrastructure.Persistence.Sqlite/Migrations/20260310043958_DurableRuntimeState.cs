using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Symphony.Infrastructure.Persistence.Sqlite.Migrations
{
    /// <inheritdoc />
    public partial class DurableRuntimeState : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "UpdatedAtUtc",
                table: "dispatch_claims",
                type: "TEXT",
                nullable: false,
                defaultValue: new DateTimeOffset(new DateTime(1, 1, 1, 0, 0, 0, 0, DateTimeKind.Unspecified), new TimeSpan(0, 0, 0, 0, 0)));

            migrationBuilder.CreateTable(
                name: "event_log",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    IssueId = table.Column<string>(type: "TEXT", maxLength: 200, nullable: true),
                    IssueIdentifier = table.Column<string>(type: "TEXT", maxLength: 200, nullable: true),
                    RunId = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true),
                    RunAttemptId = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true),
                    SessionId = table.Column<string>(type: "TEXT", maxLength: 256, nullable: true),
                    EventName = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    Level = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    Message = table.Column<string>(type: "TEXT", maxLength: 2000, nullable: false),
                    DataJson = table.Column<string>(type: "TEXT", nullable: true),
                    OccurredAtUtc = table.Column<DateTimeOffset>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_event_log", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "issues_cache",
                columns: table => new
                {
                    IssueId = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    Identifier = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    Title = table.Column<string>(type: "TEXT", maxLength: 500, nullable: false),
                    Description = table.Column<string>(type: "TEXT", nullable: true),
                    Priority = table.Column<int>(type: "INTEGER", nullable: true),
                    State = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    BranchName = table.Column<string>(type: "TEXT", maxLength: 250, nullable: true),
                    Url = table.Column<string>(type: "TEXT", maxLength: 2048, nullable: true),
                    Milestone = table.Column<string>(type: "TEXT", maxLength: 200, nullable: true),
                    LabelsJson = table.Column<string>(type: "TEXT", nullable: false),
                    PullRequestsJson = table.Column<string>(type: "TEXT", nullable: false),
                    BlockedByJson = table.Column<string>(type: "TEXT", nullable: false),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                    UpdatedAtUtc = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                    CachedAtUtc = table.Column<DateTimeOffset>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_issues_cache", x => x.IssueId);
                });

            migrationBuilder.CreateTable(
                name: "retry_queue",
                columns: table => new
                {
                    IssueId = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    IssueIdentifier = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    RunId = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    OwnerInstanceId = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    Attempt = table.Column<int>(type: "INTEGER", nullable: false),
                    DueAtUtc = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    DelayType = table.Column<string>(type: "TEXT", maxLength: 50, nullable: false),
                    Error = table.Column<string>(type: "TEXT", nullable: true),
                    MaxBackoffMs = table.Column<int>(type: "INTEGER", nullable: false),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    UpdatedAtUtc = table.Column<DateTimeOffset>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_retry_queue", x => x.IssueId);
                });

            migrationBuilder.CreateTable(
                name: "runs",
                columns: table => new
                {
                    Id = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    IssueId = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    IssueIdentifier = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    OwnerInstanceId = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    Status = table.Column<string>(type: "TEXT", maxLength: 50, nullable: false),
                    State = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    CurrentRetryAttempt = table.Column<int>(type: "INTEGER", nullable: true),
                    WorkspacePath = table.Column<string>(type: "TEXT", maxLength: 2048, nullable: true),
                    SessionId = table.Column<string>(type: "TEXT", maxLength: 256, nullable: true),
                    RequestedStopReason = table.Column<string>(type: "TEXT", maxLength: 100, nullable: true),
                    CleanupWorkspaceOnStop = table.Column<bool>(type: "INTEGER", nullable: false),
                    LastEvent = table.Column<string>(type: "TEXT", maxLength: 100, nullable: true),
                    LastMessage = table.Column<string>(type: "TEXT", nullable: true),
                    StartedAtUtc = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    LastEventAtUtc = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                    CompletedAtUtc = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                    TurnCount = table.Column<int>(type: "INTEGER", nullable: false),
                    InputTokens = table.Column<int>(type: "INTEGER", nullable: false),
                    OutputTokens = table.Column<int>(type: "INTEGER", nullable: false),
                    TotalTokens = table.Column<int>(type: "INTEGER", nullable: false),
                    LastReportedInputTokens = table.Column<int>(type: "INTEGER", nullable: false),
                    LastReportedOutputTokens = table.Column<int>(type: "INTEGER", nullable: false),
                    LastReportedTotalTokens = table.Column<int>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_runs", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "sessions",
                columns: table => new
                {
                    Id = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                    RunId = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    RunAttemptId = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    ThreadId = table.Column<string>(type: "TEXT", maxLength: 128, nullable: true),
                    TurnId = table.Column<string>(type: "TEXT", maxLength: 128, nullable: true),
                    CodexAppServerPid = table.Column<string>(type: "TEXT", maxLength: 32, nullable: true),
                    LastCodexEvent = table.Column<string>(type: "TEXT", maxLength: 100, nullable: true),
                    LastCodexTimestamp = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                    LastCodexMessage = table.Column<string>(type: "TEXT", nullable: true),
                    CodexInputTokens = table.Column<int>(type: "INTEGER", nullable: false),
                    CodexOutputTokens = table.Column<int>(type: "INTEGER", nullable: false),
                    CodexTotalTokens = table.Column<int>(type: "INTEGER", nullable: false),
                    LastReportedInputTokens = table.Column<int>(type: "INTEGER", nullable: false),
                    LastReportedOutputTokens = table.Column<int>(type: "INTEGER", nullable: false),
                    LastReportedTotalTokens = table.Column<int>(type: "INTEGER", nullable: false),
                    TurnCount = table.Column<int>(type: "INTEGER", nullable: false),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    UpdatedAtUtc = table.Column<DateTimeOffset>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_sessions", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "workflow_snapshots",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    SourcePath = table.Column<string>(type: "TEXT", maxLength: 1024, nullable: false),
                    ConfigHash = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    RuntimeJson = table.Column<string>(type: "TEXT", nullable: false),
                    LoadedAtUtc = table.Column<DateTimeOffset>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_workflow_snapshots", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "workspace_records",
                columns: table => new
                {
                    IssueId = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    IssueIdentifier = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    WorkspacePath = table.Column<string>(type: "TEXT", maxLength: 2048, nullable: false),
                    BranchName = table.Column<string>(type: "TEXT", maxLength: 250, nullable: true),
                    LastPreparedAtUtc = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    LastCleanedAtUtc = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                    LastCleanupReason = table.Column<string>(type: "TEXT", maxLength: 100, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_workspace_records", x => x.IssueId);
                });

            migrationBuilder.CreateTable(
                name: "run_attempts",
                columns: table => new
                {
                    Id = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    RunId = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    IssueId = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    AttemptNumber = table.Column<int>(type: "INTEGER", nullable: true),
                    Status = table.Column<string>(type: "TEXT", maxLength: 50, nullable: false),
                    Error = table.Column<string>(type: "TEXT", nullable: true),
                    WorkspacePath = table.Column<string>(type: "TEXT", maxLength: 2048, nullable: true),
                    StartedAtUtc = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    CompletedAtUtc = table.Column<DateTimeOffset>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_run_attempts", x => x.Id);
                    table.ForeignKey(
                        name: "FK_run_attempts_runs_RunId",
                        column: x => x.RunId,
                        principalTable: "runs",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_event_log_IssueId",
                table: "event_log",
                column: "IssueId");

            migrationBuilder.CreateIndex(
                name: "IX_event_log_OccurredAtUtc",
                table: "event_log",
                column: "OccurredAtUtc");

            migrationBuilder.CreateIndex(
                name: "IX_event_log_RunId",
                table: "event_log",
                column: "RunId");

            migrationBuilder.CreateIndex(
                name: "IX_issues_cache_Identifier",
                table: "issues_cache",
                column: "Identifier");

            migrationBuilder.CreateIndex(
                name: "IX_issues_cache_State",
                table: "issues_cache",
                column: "State");

            migrationBuilder.CreateIndex(
                name: "IX_issues_cache_UpdatedAtUtc",
                table: "issues_cache",
                column: "UpdatedAtUtc");

            migrationBuilder.CreateIndex(
                name: "IX_retry_queue_Attempt",
                table: "retry_queue",
                column: "Attempt");

            migrationBuilder.CreateIndex(
                name: "IX_retry_queue_DueAtUtc",
                table: "retry_queue",
                column: "DueAtUtc");

            migrationBuilder.CreateIndex(
                name: "IX_run_attempts_IssueId",
                table: "run_attempts",
                column: "IssueId");

            migrationBuilder.CreateIndex(
                name: "IX_run_attempts_RunId",
                table: "run_attempts",
                column: "RunId");

            migrationBuilder.CreateIndex(
                name: "IX_run_attempts_Status",
                table: "run_attempts",
                column: "Status");

            migrationBuilder.CreateIndex(
                name: "IX_runs_IssueId",
                table: "runs",
                column: "IssueId");

            migrationBuilder.CreateIndex(
                name: "IX_runs_IssueId_Status",
                table: "runs",
                columns: new[] { "IssueId", "Status" },
                unique: true,
                filter: "Status IN ('running', 'retrying')");

            migrationBuilder.CreateIndex(
                name: "IX_runs_State",
                table: "runs",
                column: "State");

            migrationBuilder.CreateIndex(
                name: "IX_runs_Status",
                table: "runs",
                column: "Status");

            migrationBuilder.CreateIndex(
                name: "IX_sessions_LastCodexTimestamp",
                table: "sessions",
                column: "LastCodexTimestamp");

            migrationBuilder.CreateIndex(
                name: "IX_sessions_RunAttemptId",
                table: "sessions",
                column: "RunAttemptId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_sessions_RunId",
                table: "sessions",
                column: "RunId");

            migrationBuilder.CreateIndex(
                name: "IX_workflow_snapshots_ConfigHash",
                table: "workflow_snapshots",
                column: "ConfigHash");

            migrationBuilder.CreateIndex(
                name: "IX_workflow_snapshots_LoadedAtUtc",
                table: "workflow_snapshots",
                column: "LoadedAtUtc");

            migrationBuilder.CreateIndex(
                name: "IX_workspace_records_IssueIdentifier",
                table: "workspace_records",
                column: "IssueIdentifier");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "event_log");

            migrationBuilder.DropTable(
                name: "issues_cache");

            migrationBuilder.DropTable(
                name: "retry_queue");

            migrationBuilder.DropTable(
                name: "run_attempts");

            migrationBuilder.DropTable(
                name: "sessions");

            migrationBuilder.DropTable(
                name: "workflow_snapshots");

            migrationBuilder.DropTable(
                name: "workspace_records");

            migrationBuilder.DropTable(
                name: "runs");

            migrationBuilder.DropColumn(
                name: "UpdatedAtUtc",
                table: "dispatch_claims");
        }
    }
}
