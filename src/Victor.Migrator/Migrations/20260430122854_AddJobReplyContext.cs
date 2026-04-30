using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Victor.Migrator.Migrations
{
    /// <inheritdoc />
    public partial class AddJobReplyContext : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "channel_id",
                schema: "victor",
                table: "jobs",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "thread_ts",
                schema: "victor",
                table: "jobs",
                type: "text",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "channel_id",
                schema: "victor",
                table: "jobs");

            migrationBuilder.DropColumn(
                name: "thread_ts",
                schema: "victor",
                table: "jobs");
        }
    }
}
