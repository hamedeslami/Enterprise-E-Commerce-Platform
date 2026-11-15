using Ecommerce.Modules.Identity.Persistence;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Ecommerce.Modules.Identity.Persistence.Migrations;

[DbContext(typeof(IdentityDbContext))]
[Migration("20261115090000_AddExplicitAccountType")]
public sealed class AddExplicitAccountType : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<int>(
            name: "AccountType",
            schema: "identity",
            table: "AspNetUsers",
            type: "integer",
            nullable: true);

        migrationBuilder.Sql("""
            DO $$
            BEGIN
                IF EXISTS (SELECT 1 FROM identity."AspNetUsers") THEN
                    RAISE EXCEPTION 'Cannot classify existing Identity users automatically. Assign Customer or Staff explicitly before applying this migration.';
                END IF;
            END $$;
            """);

        migrationBuilder.AlterColumn<int>(
            name: "AccountType",
            schema: "identity",
            table: "AspNetUsers",
            type: "integer",
            nullable: false,
            oldClrType: typeof(int),
            oldType: "integer",
            oldNullable: true);

        migrationBuilder.AddCheckConstraint(
            name: "CK_AspNetUsers_AccountType",
            schema: "identity",
            table: "AspNetUsers",
            sql: "\"AccountType\" IN (1, 2)");
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropCheckConstraint(
            name: "CK_AspNetUsers_AccountType",
            schema: "identity",
            table: "AspNetUsers");
        migrationBuilder.DropColumn(name: "AccountType", schema: "identity", table: "AspNetUsers");
    }
}
