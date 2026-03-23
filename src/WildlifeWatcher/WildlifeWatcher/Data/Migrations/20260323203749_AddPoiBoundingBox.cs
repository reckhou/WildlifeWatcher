using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace WildlifeWatcher.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddPoiBoundingBox : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<double>(
                name: "PoiNHeight",
                table: "CaptureRecords",
                type: "REAL",
                nullable: true);

            migrationBuilder.AddColumn<double>(
                name: "PoiNLeft",
                table: "CaptureRecords",
                type: "REAL",
                nullable: true);

            migrationBuilder.AddColumn<double>(
                name: "PoiNTop",
                table: "CaptureRecords",
                type: "REAL",
                nullable: true);

            migrationBuilder.AddColumn<double>(
                name: "PoiNWidth",
                table: "CaptureRecords",
                type: "REAL",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "PoiNHeight",
                table: "CaptureRecords");

            migrationBuilder.DropColumn(
                name: "PoiNLeft",
                table: "CaptureRecords");

            migrationBuilder.DropColumn(
                name: "PoiNTop",
                table: "CaptureRecords");

            migrationBuilder.DropColumn(
                name: "PoiNWidth",
                table: "CaptureRecords");
        }
    }
}
