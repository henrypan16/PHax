using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PHax.Data.Migrations
{
    /// <inheritdoc />
    public partial class NetworkClients : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "SubmissionHash",
                table: "OutboundFaxes",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "SubmittedBy",
                table: "OutboundFaxes",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "SubmittedByClientId",
                table: "OutboundFaxes",
                type: "TEXT",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "PairedClients",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    Name = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    TokenHash = table.Column<string>(type: "TEXT", nullable: false),
                    CreatedUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    LastSeenUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    LastAddress = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PairedClients", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_PairedClients_TokenHash",
                table: "PairedClients",
                column: "TokenHash",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "PairedClients");

            migrationBuilder.DropColumn(
                name: "SubmissionHash",
                table: "OutboundFaxes");

            migrationBuilder.DropColumn(
                name: "SubmittedBy",
                table: "OutboundFaxes");

            migrationBuilder.DropColumn(
                name: "SubmittedByClientId",
                table: "OutboundFaxes");
        }
    }
}
