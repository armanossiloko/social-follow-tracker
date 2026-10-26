using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ConnectionTracker.Web.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddPlatforms : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_snapshots_taken_at",
                table: "snapshots");

            migrationBuilder.AddColumn<string>(
                name: "platform",
                table: "snapshots",
                type: "character varying(16)",
                maxLength: 16,
                nullable: false,
                defaultValue: "instagram"); // everything before this migration came from Instagram

            migrationBuilder.AddColumn<string>(
                name: "platform",
                table: "runs",
                type: "character varying(16)",
                maxLength: 16,
                nullable: true);

            // Fetches and imports were all Instagram's; the daily run as a whole has no platform.
            migrationBuilder.Sql("UPDATE runs SET platform = 'instagram' WHERE kind <> 'daily';");

            migrationBuilder.CreateIndex(
                name: "ix_snapshots_platform_taken_at",
                table: "snapshots",
                columns: new[] { "platform", "taken_at" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_snapshots_platform_taken_at",
                table: "snapshots");

            migrationBuilder.DropColumn(
                name: "platform",
                table: "snapshots");

            migrationBuilder.DropColumn(
                name: "platform",
                table: "runs");

            migrationBuilder.CreateIndex(
                name: "ix_snapshots_taken_at",
                table: "snapshots",
                column: "taken_at");
        }
    }
}
