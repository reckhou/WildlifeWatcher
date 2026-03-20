using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace WildlifeWatcher.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddAnnotatedAndAlternatives : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "AnnotatedImageFilePath",
                table: "CaptureRecords",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "AlternativesJson",
                table: "CaptureRecords",
                type: "TEXT",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "AnnotatedImageFilePath",
                table: "CaptureRecords");

            migrationBuilder.DropColumn(
                name: "AlternativesJson",
                table: "CaptureRecords");
        }
    }
}
