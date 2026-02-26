using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DbSync.Core.Migrations
{
    /// <inheritdoc />
    public partial class AddNotificarCambiosToClienteAmbiente : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "NotificarCambios",
                table: "ClienteAmbientes",
                type: "bit",
                nullable: false,
                defaultValue: false);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "NotificarCambios",
                table: "ClienteAmbientes");
        }
    }
}
