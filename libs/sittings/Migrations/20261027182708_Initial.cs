using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Bistro.Sittings.Migrations
{
    /// <inheritdoc />
    public partial class Initial : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "sittings");

            migrationBuilder.CreateTable(
                name: "InboxMessages",
                schema: "sittings",
                columns: table => new
                {
                    Consumer = table.Column<string>(type: "text", nullable: false),
                    MessageId = table.Column<Guid>(type: "uuid", nullable: false),
                    ClaimedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_InboxMessages", x => new { x.Consumer, x.MessageId });
                });

            migrationBuilder.CreateTable(
                name: "OutboxMessages",
                schema: "sittings",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityAlwaysColumn),
                    MessageId = table.Column<Guid>(type: "uuid", nullable: false),
                    Module = table.Column<string>(type: "text", nullable: false),
                    RoutingKey = table.Column<string>(type: "text", nullable: false),
                    Key = table.Column<string>(type: "text", nullable: false),
                    Payload = table.Column<string>(type: "jsonb", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    SentAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_OutboxMessages", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Sittings",
                schema: "sittings",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Code = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    Name = table.Column<string>(type: "text", nullable: false),
                    Room = table.Column<string>(type: "text", nullable: false),
                    Chef = table.Column<string>(type: "text", nullable: false),
                    ServiceDate = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    PriceCents = table.Column<long>(type: "bigint", nullable: false),
                    CoversTotal = table.Column<int>(type: "integer", nullable: false),
                    CoversLeft = table.Column<int>(type: "integer", nullable: false),
                    MaxPerGuest = table.Column<int>(type: "integer", nullable: false, defaultValue: 8),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Sittings", x => x.Id);
                    table.CheckConstraint("CK_Sittings_Cap", "\"MaxPerGuest\" >= 1");
                    table.CheckConstraint("CK_Sittings_Remaining", "\"CoversLeft\" >= 0 AND \"CoversLeft\" <= \"CoversTotal\"");
                });

            migrationBuilder.CreateTable(
                name: "CoverHolds",
                schema: "sittings",
                columns: table => new
                {
                    OrderId = table.Column<Guid>(type: "uuid", nullable: false),
                    OwnerId = table.Column<Guid>(type: "uuid", nullable: false),
                    SittingId = table.Column<Guid>(type: "uuid", nullable: false),
                    Covers = table.Column<int>(type: "integer", nullable: false),
                    Status = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ReleasedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    ReleaseReason = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CoverHolds", x => x.OrderId);
                    table.ForeignKey(
                        name: "FK_CoverHolds_Sittings_SittingId",
                        column: x => x.SittingId,
                        principalSchema: "sittings",
                        principalTable: "Sittings",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "SittingGuests",
                schema: "sittings",
                columns: table => new
                {
                    SittingId = table.Column<Guid>(type: "uuid", nullable: false),
                    OwnerId = table.Column<Guid>(type: "uuid", nullable: false),
                    Covers = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SittingGuests", x => new { x.SittingId, x.OwnerId });
                    table.CheckConstraint("CK_SittingGuests_Covers", "\"Covers\" >= 0");
                    table.ForeignKey(
                        name: "FK_SittingGuests_Sittings_SittingId",
                        column: x => x.SittingId,
                        principalSchema: "sittings",
                        principalTable: "Sittings",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "Waitlist",
                schema: "sittings",
                columns: table => new
                {
                    Seq = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    OrderId = table.Column<Guid>(type: "uuid", nullable: false),
                    OwnerId = table.Column<Guid>(type: "uuid", nullable: false),
                    SittingId = table.Column<Guid>(type: "uuid", nullable: false),
                    Covers = table.Column<int>(type: "integer", nullable: false),
                    Reference = table.Column<string>(type: "text", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Waitlist", x => x.Seq);
                    table.ForeignKey(
                        name: "FK_Waitlist_Sittings_SittingId",
                        column: x => x.SittingId,
                        principalSchema: "sittings",
                        principalTable: "Sittings",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_CoverHolds_SittingId",
                schema: "sittings",
                table: "CoverHolds",
                column: "SittingId");

            migrationBuilder.CreateIndex(
                name: "IX_OutboxMessages_SentAt",
                schema: "sittings",
                table: "OutboxMessages",
                column: "SentAt",
                filter: "\"SentAt\" IS NULL");

            migrationBuilder.CreateIndex(
                name: "IX_Sittings_Code",
                schema: "sittings",
                table: "Sittings",
                column: "Code",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Waitlist_OrderId",
                schema: "sittings",
                table: "Waitlist",
                column: "OrderId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Waitlist_SittingId_Seq",
                schema: "sittings",
                table: "Waitlist",
                columns: new[] { "SittingId", "Seq" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "CoverHolds",
                schema: "sittings");

            migrationBuilder.DropTable(
                name: "InboxMessages",
                schema: "sittings");

            migrationBuilder.DropTable(
                name: "OutboxMessages",
                schema: "sittings");

            migrationBuilder.DropTable(
                name: "SittingGuests",
                schema: "sittings");

            migrationBuilder.DropTable(
                name: "Waitlist",
                schema: "sittings");

            migrationBuilder.DropTable(
                name: "Sittings",
                schema: "sittings");
        }
    }
}
