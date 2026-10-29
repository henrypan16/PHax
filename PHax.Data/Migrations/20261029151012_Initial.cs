using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PHax.Data.Migrations
{
    /// <inheritdoc />
    public partial class Initial : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "Backups",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    CreatedUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    Path = table.Column<string>(type: "TEXT", nullable: false),
                    Success = table.Column<bool>(type: "INTEGER", nullable: false),
                    Scheduled = table.Column<bool>(type: "INTEGER", nullable: false),
                    Message = table.Column<string>(type: "TEXT", nullable: true),
                    SizeBytes = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Backups", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "ModemConfigurations",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    Name = table.Column<string>(type: "TEXT", nullable: false),
                    PortName = table.Column<string>(type: "TEXT", nullable: false),
                    BaudRate = table.Column<int>(type: "INTEGER", nullable: false),
                    HardwareFlowControl = table.Column<bool>(type: "INTEGER", nullable: false),
                    ProfileId = table.Column<string>(type: "TEXT", nullable: false),
                    InitCommands = table.Column<string>(type: "TEXT", nullable: false),
                    IsActive = table.Column<bool>(type: "INTEGER", nullable: false),
                    LastIdentity = table.Column<string>(type: "TEXT", nullable: true),
                    LastSupportedClasses = table.Column<string>(type: "TEXT", nullable: true),
                    LastVerifiedUtc = table.Column<DateTime>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ModemConfigurations", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Notifications",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    CreatedUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    Kind = table.Column<int>(type: "INTEGER", nullable: false),
                    Title = table.Column<string>(type: "TEXT", nullable: false),
                    Message = table.Column<string>(type: "TEXT", nullable: false),
                    RelatedId = table.Column<Guid>(type: "TEXT", nullable: true),
                    Delivered = table.Column<bool>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Notifications", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "OutboundFaxes",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    CreatedUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    UpdatedUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    Status = table.Column<int>(type: "INTEGER", nullable: false),
                    RecipientName = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    FaxNumber = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    Subject = table.Column<string>(type: "TEXT", maxLength: 500, nullable: false),
                    IncludeCoverPage = table.Column<bool>(type: "INTEGER", nullable: false),
                    CoverMessage = table.Column<string>(type: "TEXT", nullable: false),
                    DocumentPath = table.Column<string>(type: "TEXT", nullable: true),
                    PageCount = table.Column<int>(type: "INTEGER", nullable: false),
                    Attempts = table.Column<int>(type: "INTEGER", nullable: false),
                    MaxAttempts = table.Column<int>(type: "INTEGER", nullable: false),
                    NextAttemptUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    QueuedUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    CurrentPage = table.Column<int>(type: "INTEGER", nullable: false),
                    PagesConfirmed = table.Column<int>(type: "INTEGER", nullable: false),
                    LastError = table.Column<string>(type: "TEXT", nullable: true),
                    LastOutcome = table.Column<int>(type: "INTEGER", nullable: true),
                    SentUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    DurationSeconds = table.Column<double>(type: "REAL", nullable: true),
                    RemoteId = table.Column<string>(type: "TEXT", nullable: true),
                    ConfirmationDetail = table.Column<string>(type: "TEXT", nullable: true),
                    IsSimulated = table.Column<bool>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_OutboundFaxes", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "ReceivedFaxes",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    ReceivedUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    Status = table.Column<int>(type: "INTEGER", nullable: false),
                    DisplayName = table.Column<string>(type: "TEXT", nullable: false),
                    CallerId = table.Column<string>(type: "TEXT", nullable: true),
                    CallerName = table.Column<string>(type: "TEXT", nullable: true),
                    RemoteId = table.Column<string>(type: "TEXT", nullable: true),
                    PageCount = table.Column<int>(type: "INTEGER", nullable: false),
                    TiffPath = table.Column<string>(type: "TEXT", nullable: true),
                    PdfPath = table.Column<string>(type: "TEXT", nullable: true),
                    IsRead = table.Column<bool>(type: "INTEGER", nullable: false),
                    DurationSeconds = table.Column<double>(type: "REAL", nullable: true),
                    Modulation = table.Column<string>(type: "TEXT", nullable: true),
                    Resolution = table.Column<int>(type: "INTEGER", nullable: true),
                    ErrorMessage = table.Column<string>(type: "TEXT", nullable: true),
                    ProtocolLogPath = table.Column<string>(type: "TEXT", nullable: true),
                    IsSimulated = table.Column<bool>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ReceivedFaxes", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Recipients",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    Name = table.Column<string>(type: "TEXT", nullable: false),
                    FaxNumber = table.Column<string>(type: "TEXT", nullable: false),
                    LastUsedUtc = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Recipients", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Settings",
                columns: table => new
                {
                    Key = table.Column<string>(type: "TEXT", nullable: false),
                    Value = table.Column<string>(type: "TEXT", nullable: false),
                    UpdatedUtc = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Settings", x => x.Key);
                });

            migrationBuilder.CreateTable(
                name: "Attachments",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    OutboundFaxId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Order = table.Column<int>(type: "INTEGER", nullable: false),
                    OriginalFileName = table.Column<string>(type: "TEXT", nullable: false),
                    StoredPath = table.Column<string>(type: "TEXT", nullable: false),
                    ContentType = table.Column<string>(type: "TEXT", nullable: false),
                    PageCount = table.Column<int>(type: "INTEGER", nullable: false),
                    SizeBytes = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Attachments", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Attachments_OutboundFaxes_OutboundFaxId",
                        column: x => x.OutboundFaxId,
                        principalTable: "OutboundFaxes",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "TransmissionAttempts",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    OutboundFaxId = table.Column<Guid>(type: "TEXT", nullable: false),
                    AttemptNumber = table.Column<int>(type: "INTEGER", nullable: false),
                    StartedUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    EndedUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    Outcome = table.Column<int>(type: "INTEGER", nullable: true),
                    ErrorCode = table.Column<string>(type: "TEXT", nullable: true),
                    Message = table.Column<string>(type: "TEXT", nullable: true),
                    Modulation = table.Column<string>(type: "TEXT", nullable: true),
                    Resolution = table.Column<int>(type: "INTEGER", nullable: true),
                    PagesSent = table.Column<int>(type: "INTEGER", nullable: false),
                    PagesConfirmed = table.Column<int>(type: "INTEGER", nullable: false),
                    RemoteId = table.Column<string>(type: "TEXT", nullable: true),
                    PageDataTransmitted = table.Column<bool>(type: "INTEGER", nullable: false),
                    ProtocolLogPath = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TransmissionAttempts", x => x.Id);
                    table.ForeignKey(
                        name: "FK_TransmissionAttempts_OutboundFaxes_OutboundFaxId",
                        column: x => x.OutboundFaxId,
                        principalTable: "OutboundFaxes",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Attachments_OutboundFaxId",
                table: "Attachments",
                column: "OutboundFaxId");

            migrationBuilder.CreateIndex(
                name: "IX_Backups_CreatedUtc",
                table: "Backups",
                column: "CreatedUtc");

            migrationBuilder.CreateIndex(
                name: "IX_Notifications_CreatedUtc",
                table: "Notifications",
                column: "CreatedUtc");

            migrationBuilder.CreateIndex(
                name: "IX_OutboundFaxes_NextAttemptUtc",
                table: "OutboundFaxes",
                column: "NextAttemptUtc");

            migrationBuilder.CreateIndex(
                name: "IX_OutboundFaxes_Status",
                table: "OutboundFaxes",
                column: "Status");

            migrationBuilder.CreateIndex(
                name: "IX_ReceivedFaxes_ReceivedUtc",
                table: "ReceivedFaxes",
                column: "ReceivedUtc");

            migrationBuilder.CreateIndex(
                name: "IX_ReceivedFaxes_Status",
                table: "ReceivedFaxes",
                column: "Status");

            migrationBuilder.CreateIndex(
                name: "IX_Recipients_FaxNumber",
                table: "Recipients",
                column: "FaxNumber");

            migrationBuilder.CreateIndex(
                name: "IX_TransmissionAttempts_OutboundFaxId",
                table: "TransmissionAttempts",
                column: "OutboundFaxId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "Attachments");

            migrationBuilder.DropTable(
                name: "Backups");

            migrationBuilder.DropTable(
                name: "ModemConfigurations");

            migrationBuilder.DropTable(
                name: "Notifications");

            migrationBuilder.DropTable(
                name: "ReceivedFaxes");

            migrationBuilder.DropTable(
                name: "Recipients");

            migrationBuilder.DropTable(
                name: "Settings");

            migrationBuilder.DropTable(
                name: "TransmissionAttempts");

            migrationBuilder.DropTable(
                name: "OutboundFaxes");
        }
    }
}
