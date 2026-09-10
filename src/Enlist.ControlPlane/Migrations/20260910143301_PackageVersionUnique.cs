using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Enlist.ControlPlane.Migrations
{
    /// <inheritdoc />
    public partial class PackageVersionUnique : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterColumn<string>(
                name: "ApplicationName",
                table: "Packages",
                type: "nvarchar(450)",
                nullable: true,
                oldClrType: typeof(string),
                oldType: "nvarchar(max)",
                oldNullable: true);

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
            migrationBuilder.DropIndex(
                name: "IX_Packages_ApplicationName_VersionNumber",
                table: "Packages");

            migrationBuilder.AlterColumn<string>(
                name: "ApplicationName",
                table: "Packages",
                type: "nvarchar(max)",
                nullable: true,
                oldClrType: typeof(string),
                oldType: "nvarchar(450)",
                oldNullable: true);
        }
    }
}
