using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace OrderService.Data.Migrations;

/// <summary>
/// Initial schema migration for Azure SQL (Business Critical).
///
/// Apply via: dotnet ef database update
/// Or at startup: app.Services.GetRequiredService&lt;OrderDbContext&gt;().Database.Migrate()
///
/// NOTE: Never apply migrations against the read-replica endpoint.
/// The secondary replica is read-only and mirrors the primary automatically
/// via Azure SQL Business Critical's Always On availability group.
/// </summary>
public partial class InitialSchema : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "Orders",
            columns: table => new
            {
                Id            = table.Column<Guid>(nullable: false, defaultValueSql: "NEWSEQUENTIALID()"),
                CustomerEmail = table.Column<string>(maxLength: 256, nullable: false),
                CustomerName  = table.Column<string>(maxLength: 256, nullable: false),
                Region        = table.Column<string>(maxLength: 10,  nullable: false),
                Status        = table.Column<string>(maxLength: 50,  nullable: false, defaultValue: "Queued"),
                CorrelationId = table.Column<Guid>(nullable: false),
                CreatedAt     = table.Column<DateTime>(nullable: false, defaultValueSql: "GETUTCDATE()"),
                UpdatedAt     = table.Column<DateTime>(nullable: true),
                FulfilledAt   = table.Column<DateTime>(nullable: true)
            },
            constraints: table => table.PrimaryKey("PK_Orders", x => x.Id));

        migrationBuilder.CreateTable(
            name: "OrderItems",
            columns: table => new
            {
                Id          = table.Column<Guid>(nullable: false, defaultValueSql: "NEWSEQUENTIALID()"),
                OrderId     = table.Column<Guid>(nullable: false),
                ProductId   = table.Column<Guid>(nullable: false),
                Sku         = table.Column<string>(maxLength: 100, nullable: false),
                ProductName = table.Column<string>(maxLength: 256, nullable: false),
                UnitPrice   = table.Column<decimal>(type: "decimal(18,2)", nullable: false),
                Quantity    = table.Column<int>(nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_OrderItems", x => x.Id);
                table.ForeignKey(
                    name: "FK_OrderItems_Orders_OrderId",
                    column: x => x.OrderId,
                    principalTable: "Orders",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Cascade);
            });

        // ── Indexes ────────────────────────────────────────────────────────
        // Idempotency gate (unique): Worker Function checks this before insert
        migrationBuilder.CreateIndex("IX_Orders_CorrelationId", "Orders", "CorrelationId", unique: true);

        // Customer history lookups
        migrationBuilder.CreateIndex("IX_Orders_CustomerEmail", "Orders", "CustomerEmail");

        // Cosmos DB CDC partition queries
        migrationBuilder.CreateIndex("IX_Orders_Region_CreatedAt", "Orders", ["Region", "CreatedAt"]);

        // Status polling by Worker Function and dashboards
        migrationBuilder.CreateIndex("IX_Orders_Status", "Orders", "Status");

        // Order item lookups
        migrationBuilder.CreateIndex("IX_OrderItems_OrderId",  "OrderItems", "OrderId");
        migrationBuilder.CreateIndex("IX_OrderItems_Sku",      "OrderItems", "Sku");
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable("OrderItems");
        migrationBuilder.DropTable("Orders");
    }
}