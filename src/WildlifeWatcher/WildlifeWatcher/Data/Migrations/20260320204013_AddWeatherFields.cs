using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace WildlifeWatcher.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddWeatherFields : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<double>(
                name: "Precipitation",
                table: "CaptureRecords",
                type: "REAL",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "Sunrise",
                table: "CaptureRecords",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "Sunset",
                table: "CaptureRecords",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<double>(
                name: "Temperature",
                table: "CaptureRecords",
                type: "REAL",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "WeatherCondition",
                table: "CaptureRecords",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<double>(
                name: "WindSpeed",
                table: "CaptureRecords",
                type: "REAL",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Precipitation",
                table: "CaptureRecords");

            migrationBuilder.DropColumn(
                name: "Sunrise",
                table: "CaptureRecords");

            migrationBuilder.DropColumn(
                name: "Sunset",
                table: "CaptureRecords");

            migrationBuilder.DropColumn(
                name: "Temperature",
                table: "CaptureRecords");

            migrationBuilder.DropColumn(
                name: "WeatherCondition",
                table: "CaptureRecords");

            migrationBuilder.DropColumn(
                name: "WindSpeed",
                table: "CaptureRecords");
        }
    }
}
