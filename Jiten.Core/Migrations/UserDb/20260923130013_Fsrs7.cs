using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Jiten.Core.Migrations.UserDb
{
    /// <inheritdoc />
    public partial class Fsrs7 : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "PreviousParametersJson",
                schema: "user",
                table: "UserFsrsSettings",
                type: "jsonb",
                nullable: true);

            migrationBuilder.AddColumn<double>(
                name: "StabilityFast",
                schema: "user",
                table: "FsrsCards",
                type: "double precision",
                nullable: true);

            migrationBuilder.AddColumn<double>(
                name: "StabilityFast",
                schema: "user",
                table: "FsrsCardArchives",
                type: "double precision",
                nullable: true);

            // Saving only a desired retention used to store the FSRS-6 defaults explicitly; those users never
            // optimised, so they must follow FsrsVersions.Unoptimised like everyone on "[]".
            migrationBuilder.Sql("""
                UPDATE "user"."UserFsrsSettings"
                SET "ParametersJson" = '[]'::jsonb
                WHERE "ParametersJson" = '[0.212, 1.2931, 2.3065, 8.2956, 6.4133, 0.8334, 3.0194, 0.001, 1.8722, 0.1666, 0.796, 1.4835, 0.0614, 0.2629, 1.6483, 0.6014, 1.8729, 0.5425, 0.0912, 0.0658, 0.1542]'::jsonb;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "PreviousParametersJson",
                schema: "user",
                table: "UserFsrsSettings");

            migrationBuilder.DropColumn(
                name: "StabilityFast",
                schema: "user",
                table: "FsrsCards");

            migrationBuilder.DropColumn(
                name: "StabilityFast",
                schema: "user",
                table: "FsrsCardArchives");
        }
    }
}
