using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace WildlifeWatcher.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddReferencePhotoPath : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "ReferencePhotoPath",
                table: "Species",
                type: "TEXT",
                maxLength: 500,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ReferencePhotoPath",
                table: "Species");
        }
    }
}
