using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SolarPortal.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class Phase3OperationsRemarkAndAssignment : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "Remark",
                table: "MeterDispatches",
                type: "nvarchar(max)",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "AssignedWorkerId",
                table: "MaterialDispatches",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Remark",
                table: "MaterialDispatches",
                type: "nvarchar(max)",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "AssignedWorkerId",
                table: "Installations",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Remark",
                table: "Installations",
                type: "nvarchar(max)",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Remark",
                table: "DCRDocuments",
                type: "nvarchar(max)",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_MaterialDispatches_AssignedWorkerId",
                table: "MaterialDispatches",
                column: "AssignedWorkerId");

            migrationBuilder.CreateIndex(
                name: "IX_Installations_AssignedWorkerId",
                table: "Installations",
                column: "AssignedWorkerId");

            migrationBuilder.AddForeignKey(
                name: "FK_Installations_Workers_AssignedWorkerId",
                table: "Installations",
                column: "AssignedWorkerId",
                principalTable: "Workers",
                principalColumn: "Id");

            migrationBuilder.AddForeignKey(
                name: "FK_MaterialDispatches_Workers_AssignedWorkerId",
                table: "MaterialDispatches",
                column: "AssignedWorkerId",
                principalTable: "Workers",
                principalColumn: "Id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_Installations_Workers_AssignedWorkerId",
                table: "Installations");

            migrationBuilder.DropForeignKey(
                name: "FK_MaterialDispatches_Workers_AssignedWorkerId",
                table: "MaterialDispatches");

            migrationBuilder.DropIndex(
                name: "IX_MaterialDispatches_AssignedWorkerId",
                table: "MaterialDispatches");

            migrationBuilder.DropIndex(
                name: "IX_Installations_AssignedWorkerId",
                table: "Installations");

            migrationBuilder.DropColumn(
                name: "Remark",
                table: "MeterDispatches");

            migrationBuilder.DropColumn(
                name: "AssignedWorkerId",
                table: "MaterialDispatches");

            migrationBuilder.DropColumn(
                name: "Remark",
                table: "MaterialDispatches");

            migrationBuilder.DropColumn(
                name: "AssignedWorkerId",
                table: "Installations");

            migrationBuilder.DropColumn(
                name: "Remark",
                table: "Installations");

            migrationBuilder.DropColumn(
                name: "Remark",
                table: "DCRDocuments");
        }
    }
}
