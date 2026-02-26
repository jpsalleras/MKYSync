using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DbSync.Core.Migrations
{
    /// <inheritdoc />
    public partial class AddObjetosBase : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ObjetosBase",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    NombreObjeto = table.Column<string>(type: "nvarchar(300)", maxLength: 300, nullable: false),
                    TipoObjeto = table.Column<string>(type: "nvarchar(10)", maxLength: 10, nullable: false),
                    Notas = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ObjetosBase", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ObjetosBase_NombreObjeto_TipoObjeto",
                table: "ObjetosBase",
                columns: new[] { "NombreObjeto", "TipoObjeto" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ObjetosBase");
        }
    }
}
