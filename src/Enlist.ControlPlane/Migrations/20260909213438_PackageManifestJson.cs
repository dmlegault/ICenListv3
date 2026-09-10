using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Enlist.ControlPlane.Migrations
{
    /// <inheritdoc />
    public partial class PackageManifestJson : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "ManifestJson",
                table: "Packages",
                type: "nvarchar(max)",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ManifestJson",
                table: "Packages");
        }
    }
}
