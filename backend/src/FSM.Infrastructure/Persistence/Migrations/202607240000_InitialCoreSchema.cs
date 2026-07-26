using FSM.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FSM.Infrastructure.Persistence.Migrations;

[DbContext(typeof(FsmDbContext))]
[Migration("202607240000_InitialCoreSchema")]
public sealed class InitialCoreSchema : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder) => migrationBuilder.Sql("""
        CREATE TABLE "users" (
          "Id" TEXT NOT NULL CONSTRAINT "PK_users" PRIMARY KEY,
          "FullName" TEXT NOT NULL, "Email" TEXT NOT NULL,
          "PasswordHash" TEXT NOT NULL, "Role" TEXT NOT NULL, "IsActive" INTEGER NOT NULL
        );
        CREATE UNIQUE INDEX "IX_users_Email" ON "users" ("Email");
        CREATE TABLE "vessels" (
          "Id" TEXT NOT NULL CONSTRAINT "PK_vessels" PRIMARY KEY,
          "VesselName" TEXT NOT NULL, "ImoNumber" TEXT NOT NULL, "Flag" TEXT NULL,
          "CompanyName" TEXT NULL, "InvoicingDetails" TEXT NULL, "AccountingEmail" TEXT NULL
        );
        CREATE UNIQUE INDEX "IX_vessels_ImoNumber" ON "vessels" ("ImoNumber");
        CREATE TABLE "user_vessels" (
          "UserId" TEXT NOT NULL, "VesselId" TEXT NOT NULL,
          CONSTRAINT "PK_user_vessels" PRIMARY KEY ("UserId", "VesselId"),
          CONSTRAINT "FK_user_vessels_users_UserId" FOREIGN KEY ("UserId") REFERENCES "users" ("Id") ON DELETE CASCADE,
          CONSTRAINT "FK_user_vessels_vessels_VesselId" FOREIGN KEY ("VesselId") REFERENCES "vessels" ("Id") ON DELETE CASCADE
        );
        CREATE INDEX "IX_user_vessels_VesselId" ON "user_vessels" ("VesselId");
        CREATE TABLE "equipment" (
          "Id" TEXT NOT NULL CONSTRAINT "PK_equipment" PRIMARY KEY,
          "VesselId" TEXT NOT NULL, "Category" TEXT NOT NULL, "EquipmentName" TEXT NOT NULL,
          "Maker" TEXT NULL, "Model" TEXT NULL, "SerialNumber" TEXT NULL,
          "Quantity" INTEGER NOT NULL, "InspectionBy" TEXT NOT NULL, "ExpiryDate" TEXT NULL,
          "Remarks" TEXT NULL, "CurrentCertificatePath" TEXT NULL,
          "CurrentCertificateFileName" TEXT NULL, "CurrentReportPath" TEXT NULL,
          "CurrentReportFileName" TEXT NULL,
          CONSTRAINT "FK_equipment_vessels_VesselId" FOREIGN KEY ("VesselId") REFERENCES "vessels" ("Id") ON DELETE CASCADE
        );
        CREATE INDEX "IX_equipment_ExpiryDate" ON "equipment" ("ExpiryDate");
        CREATE INDEX "IX_equipment_VesselId_Category" ON "equipment" ("VesselId", "Category");
        """);

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable("user_vessels");
        migrationBuilder.DropTable("equipment");
        migrationBuilder.DropTable("users");
        migrationBuilder.DropTable("vessels");
    }
}
