using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SoR.DeviceDirectory.Data.Migrations
{
    /// <inheritdoc />
    public partial class Initial : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "device_directory");

            migrationBuilder.CreateTable(
                name: "devices",
                schema: "device_directory",
                columns: table => new
                {
                    id = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    tenant_id = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    hostname = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    os = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    ip_address = table.Column<string>(type: "character varying(45)", maxLength: 45, nullable: false),
                    last_seen_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_devices", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "seed_state",
                schema: "device_directory",
                columns: table => new
                {
                    key = table.Column<string>(type: "text", nullable: false),
                    completed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    row_count = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_seed_state", x => x.key);
                });

            migrationBuilder.CreateIndex(
                name: "IX_devices_tenant_id_hostname",
                schema: "device_directory",
                table: "devices",
                columns: new[] { "tenant_id", "hostname" });

            migrationBuilder.CreateIndex(
                name: "IX_devices_tenant_id_os",
                schema: "device_directory",
                table: "devices",
                columns: new[] { "tenant_id", "os" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "devices",
                schema: "device_directory");

            migrationBuilder.DropTable(
                name: "seed_state",
                schema: "device_directory");
        }
    }
}
