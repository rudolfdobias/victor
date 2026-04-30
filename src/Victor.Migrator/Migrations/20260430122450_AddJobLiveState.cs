using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Victor.Migrator.Migrations
{
    /// <inheritdoc />
    public partial class AddJobLiveState : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "current_phase",
                schema: "victor",
                table: "jobs",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "last_status_message",
                schema: "victor",
                table: "jobs",
                type: "text",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "current_phase",
                schema: "victor",
                table: "jobs");

            migrationBuilder.DropColumn(
                name: "last_status_message",
                schema: "victor",
                table: "jobs");
        }
    }
}
