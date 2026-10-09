using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace OpsLookup.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddAttorney : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "AttorneyId",
                table: "Party",
                type: "int",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "Attorney",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    Name = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    Status = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    CreatedDate = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Attorney", x => x.Id);
                    table.CheckConstraint("CK_Attorney_Status", "[Status] IN ('approved', 'faked')");
                });

            migrationBuilder.CreateIndex(
                name: "IX_Party_AttorneyId",
                table: "Party",
                column: "AttorneyId");

            migrationBuilder.CreateIndex(
                name: "IX_Party_Name",
                table: "Party",
                column: "Name");

            migrationBuilder.CreateIndex(
                name: "IX_Attorney_Name",
                table: "Attorney",
                column: "Name");

            migrationBuilder.AddForeignKey(
                name: "FK_Party_Attorney_AttorneyId",
                table: "Party",
                column: "AttorneyId",
                principalTable: "Attorney",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_Party_Attorney_AttorneyId",
                table: "Party");

            migrationBuilder.DropTable(
                name: "Attorney");

            migrationBuilder.DropIndex(
                name: "IX_Party_AttorneyId",
                table: "Party");

            migrationBuilder.DropIndex(
                name: "IX_Party_Name",
                table: "Party");

            migrationBuilder.DropColumn(
                name: "AttorneyId",
                table: "Party");
        }
    }
}
