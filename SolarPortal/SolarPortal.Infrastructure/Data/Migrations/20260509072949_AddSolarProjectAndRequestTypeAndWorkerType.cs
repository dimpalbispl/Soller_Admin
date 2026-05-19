using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SolarPortal.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddSolarProjectAndRequestTypeAndWorkerType : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "Type",
                table: "Workers",
                type: "int",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AlterColumn<decimal>(
                name: "KVCapacity",
                table: "SolarRequests",
                type: "decimal(8,2)",
                nullable: false,
                oldClrType: typeof(decimal),
                oldType: "decimal(18,2)");

            migrationBuilder.AddColumn<int>(
                name: "RequestType",
                table: "SolarRequests",
                type: "int",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "SolarProjectId",
                table: "SolarRequests",
                type: "int",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "SolarProjects",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    Name = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    SolarTypeKV = table.Column<decimal>(type: "decimal(8,2)", nullable: false),
                    ConnectionType = table.Column<int>(type: "int", nullable: false),
                    BV = table.Column<int>(type: "int", nullable: false),
                    FinalBV = table.Column<int>(type: "int", nullable: false),
                    DiscomWork = table.Column<decimal>(type: "decimal(18,2)", nullable: false),
                    DealClose = table.Column<decimal>(type: "decimal(18,2)", nullable: false),
                    SCZMenue = table.Column<decimal>(type: "decimal(18,2)", nullable: false),
                    SportainTeam = table.Column<decimal>(type: "decimal(18,2)", nullable: false),
                    TotalAmount = table.Column<decimal>(type: "decimal(18,2)", nullable: false),
                    IsActive = table.Column<bool>(type: "bit", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    CreatedBy = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    UpdatedBy = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    IsDeleted = table.Column<bool>(type: "bit", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SolarProjects", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_SolarRequests_SolarProjectId",
                table: "SolarRequests",
                column: "SolarProjectId");

            migrationBuilder.AddForeignKey(
                name: "FK_SolarRequests_SolarProjects_SolarProjectId",
                table: "SolarRequests",
                column: "SolarProjectId",
                principalTable: "SolarProjects",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_SolarRequests_SolarProjects_SolarProjectId",
                table: "SolarRequests");

            migrationBuilder.DropTable(
                name: "SolarProjects");

            migrationBuilder.DropIndex(
                name: "IX_SolarRequests_SolarProjectId",
                table: "SolarRequests");

            migrationBuilder.DropColumn(
                name: "Type",
                table: "Workers");

            migrationBuilder.DropColumn(
                name: "RequestType",
                table: "SolarRequests");

            migrationBuilder.DropColumn(
                name: "SolarProjectId",
                table: "SolarRequests");

            migrationBuilder.AlterColumn<decimal>(
                name: "KVCapacity",
                table: "SolarRequests",
                type: "decimal(18,2)",
                nullable: false,
                oldClrType: typeof(decimal),
                oldType: "decimal(8,2)");
        }
    }
}
