using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Enlist.ControlPlane.Migrations
{
    /// <inheritdoc />
    public partial class InitialCreate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "AgentLogs",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    AgentName = table.Column<string>(type: "nvarchar(450)", nullable: false),
                    ApplicationName = table.Column<string>(type: "nvarchar(450)", nullable: false),
                    Level = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    Source = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    Text = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    TimestampUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AgentLogs", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "AgentReports",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    AgentName = table.Column<string>(type: "nvarchar(450)", nullable: false),
                    SnapshotJson = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    ReportedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AgentReports", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Agents",
                columns: table => new
                {
                    Name = table.Column<string>(type: "nvarchar(450)", nullable: false),
                    TagsJson = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    FirstSeenUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    LastSeenUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    SchedulingEnabled = table.Column<bool>(type: "bit", nullable: false),
                    CapabilitiesJson = table.Column<string>(type: "nvarchar(max)", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Agents", x => x.Name);
                });

            migrationBuilder.CreateTable(
                name: "ApiKeys",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Name = table.Column<string>(type: "nvarchar(450)", nullable: false),
                    KeyHash = table.Column<string>(type: "nvarchar(450)", nullable: false),
                    Role = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    CreatedBy = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    ExpiresAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    LastUsedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    RevokedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ApiKeys", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "ApplicationPolicies",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TagSelectorJson = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    ApplicationName = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    Path = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    PackageDigest = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    DesiredState = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    CronOverridesJson = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    UpdatedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    RuntimeFlavor = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    IsolationJson = table.Column<string>(type: "nvarchar(max)", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ApplicationPolicies", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "JoinTokens",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TokenHash = table.Column<string>(type: "nvarchar(450)", nullable: false),
                    CreatedBy = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    ExpiresAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    UsesRemaining = table.Column<int>(type: "int", nullable: true),
                    RevokedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_JoinTokens", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Packages",
                columns: table => new
                {
                    Digest = table.Column<string>(type: "nvarchar(450)", nullable: false),
                    SizeBytes = table.Column<long>(type: "bigint", nullable: false),
                    UploadedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    ApplicationName = table.Column<string>(type: "nvarchar(450)", nullable: true),
                    VersionNumber = table.Column<int>(type: "int", nullable: true),
                    UnreferencedSinceUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    RuntimeFlavor = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    ManifestJson = table.Column<string>(type: "nvarchar(max)", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Packages", x => x.Digest);
                });

            migrationBuilder.CreateTable(
                name: "AgentCredentials",
                columns: table => new
                {
                    AgentName = table.Column<string>(type: "nvarchar(450)", nullable: false),
                    TokenHash = table.Column<string>(type: "nvarchar(450)", nullable: false),
                    IssuedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    LastUsedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    RevokedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AgentCredentials", x => x.AgentName);
                    table.ForeignKey(
                        name: "FK_AgentCredentials_Agents_AgentName",
                        column: x => x.AgentName,
                        principalTable: "Agents",
                        principalColumn: "Name",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_AgentCredentials_TokenHash",
                table: "AgentCredentials",
                column: "TokenHash",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_AgentLogs_AgentName_ApplicationName_Id",
                table: "AgentLogs",
                columns: new[] { "AgentName", "ApplicationName", "Id" });

            migrationBuilder.CreateIndex(
                name: "IX_AgentReports_AgentName_ReportedAtUtc",
                table: "AgentReports",
                columns: new[] { "AgentName", "ReportedAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_ApiKeys_KeyHash",
                table: "ApiKeys",
                column: "KeyHash",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ApiKeys_Name",
                table: "ApiKeys",
                column: "Name",
                unique: true,
                filter: "[RevokedAtUtc] IS NULL");

            migrationBuilder.CreateIndex(
                name: "IX_JoinTokens_TokenHash",
                table: "JoinTokens",
                column: "TokenHash",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Packages_ApplicationName_VersionNumber",
                table: "Packages",
                columns: new[] { "ApplicationName", "VersionNumber" },
                unique: true,
                filter: "[ApplicationName] IS NOT NULL AND [VersionNumber] IS NOT NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "AgentCredentials");

            migrationBuilder.DropTable(
                name: "AgentLogs");

            migrationBuilder.DropTable(
                name: "AgentReports");

            migrationBuilder.DropTable(
                name: "ApiKeys");

            migrationBuilder.DropTable(
                name: "ApplicationPolicies");

            migrationBuilder.DropTable(
                name: "JoinTokens");

            migrationBuilder.DropTable(
                name: "Packages");

            migrationBuilder.DropTable(
                name: "Agents");
        }
    }
}
