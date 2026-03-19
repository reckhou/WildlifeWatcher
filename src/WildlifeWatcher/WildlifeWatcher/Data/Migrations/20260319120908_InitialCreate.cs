using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace WildlifeWatcher.Data.Migrations
{
    /// <inheritdoc />
    public partial class InitialCreate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "Species",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    CommonName = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    ScientificName = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    Description = table.Column<string>(type: "TEXT", nullable: true),
                    FirstDetectedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Species", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "CaptureRecords",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    SpeciesId = table.Column<int>(type: "INTEGER", nullable: false),
                    CapturedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    ImageFilePath = table.Column<string>(type: "TEXT", maxLength: 500, nullable: false),
                    AiRawResponse = table.Column<string>(type: "TEXT", nullable: false),
                    ConfidenceScore = table.Column<double>(type: "REAL", nullable: false),
                    Notes = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CaptureRecords", x => x.Id);
                    table.ForeignKey(
                        name: "FK_CaptureRecords_Species_SpeciesId",
                        column: x => x.SpeciesId,
                        principalTable: "Species",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_CaptureRecords_SpeciesId",
                table: "CaptureRecords",
                column: "SpeciesId");

            migrationBuilder.CreateIndex(
                name: "IX_Species_CommonName",
                table: "Species",
                column: "CommonName");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "CaptureRecords");

            migrationBuilder.DropTable(
                name: "Species");
        }
    }
}
