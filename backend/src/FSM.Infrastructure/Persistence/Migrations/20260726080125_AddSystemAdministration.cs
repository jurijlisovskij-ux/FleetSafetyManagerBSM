using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FSM.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddSystemAdministration : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "system_audit_entries",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    CreatedUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    UserId = table.Column<Guid>(type: "TEXT", nullable: true),
                    UserName = table.Column<string>(type: "TEXT", maxLength: 180, nullable: false),
                    Action = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    ObjectType = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    ObjectId = table.Column<string>(type: "TEXT", maxLength: 180, nullable: true),
                    Details = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_system_audit_entries", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "system_backups",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    BackupType = table.Column<string>(type: "TEXT", maxLength: 40, nullable: false),
                    FileName = table.Column<string>(type: "TEXT", maxLength: 260, nullable: false),
                    FileSize = table.Column<long>(type: "INTEGER", nullable: false),
                    CreatedUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    CreatedByUserId = table.Column<Guid>(type: "TEXT", nullable: false),
                    CreatedByName = table.Column<string>(type: "TEXT", maxLength: 180, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_system_backups", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "system_settings",
                columns: table => new
                {
                    Key = table.Column<string>(type: "TEXT", maxLength: 120, nullable: false),
                    Value = table.Column<string>(type: "TEXT", maxLength: 4000, nullable: false),
                    UpdatedUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    UpdatedByUserId = table.Column<Guid>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_system_settings", x => x.Key);
                });

            migrationBuilder.CreateIndex(
                name: "IX_system_audit_entries_Action_CreatedUtc",
                table: "system_audit_entries",
                columns: new[] { "Action", "CreatedUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_system_audit_entries_CreatedUtc",
                table: "system_audit_entries",
                column: "CreatedUtc");

            migrationBuilder.CreateIndex(
                name: "IX_system_audit_entries_UserId_CreatedUtc",
                table: "system_audit_entries",
                columns: new[] { "UserId", "CreatedUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_system_backups_CreatedUtc",
                table: "system_backups",
                column: "CreatedUtc");

            migrationBuilder.CreateIndex(
                name: "IX_system_settings_UpdatedUtc",
                table: "system_settings",
                column: "UpdatedUtc");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "system_audit_entries");

            migrationBuilder.DropTable(
                name: "system_backups");

            migrationBuilder.DropTable(
                name: "system_settings");
        }
    }
}
