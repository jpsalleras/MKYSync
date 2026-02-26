using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DbSync.Core.Migrations
{
    /// <inheritdoc />
    public partial class AddObjetoBaseClienteId : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_ObjetosBase_NombreObjeto_TipoObjeto",
                table: "ObjetosBase");

            migrationBuilder.AddColumn<int>(
                name: "ClienteId",
                table: "ObjetosBase",
                type: "int",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_ObjetosBase_ClienteId_NombreObjeto_TipoObjeto",
                table: "ObjetosBase",
                columns: new[] { "ClienteId", "NombreObjeto", "TipoObjeto" },
                unique: true);

            migrationBuilder.AddForeignKey(
                name: "FK_ObjetosBase_Clientes_ClienteId",
                table: "ObjetosBase",
                column: "ClienteId",
                principalTable: "Clientes",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_ObjetosBase_Clientes_ClienteId",
                table: "ObjetosBase");

            migrationBuilder.DropIndex(
                name: "IX_ObjetosBase_ClienteId_NombreObjeto_TipoObjeto",
                table: "ObjetosBase");

            migrationBuilder.DropColumn(
                name: "ClienteId",
                table: "ObjetosBase");

            migrationBuilder.CreateIndex(
                name: "IX_ObjetosBase_NombreObjeto_TipoObjeto",
                table: "ObjetosBase",
                columns: new[] { "NombreObjeto", "TipoObjeto" },
                unique: true);
        }
    }
}
