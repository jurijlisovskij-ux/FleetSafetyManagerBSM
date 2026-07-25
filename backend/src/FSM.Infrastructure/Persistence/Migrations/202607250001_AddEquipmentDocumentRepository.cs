using FSM.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FSM.Infrastructure.Persistence.Migrations;

[DbContext(typeof(FsmDbContext))]
[Migration("202607250001_AddEquipmentDocumentRepository")]
public sealed class AddEquipmentDocumentRepository : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql(
            """
            CREATE TABLE IF NOT EXISTS "equipment_documents" (
                "Id" TEXT NOT NULL CONSTRAINT "PK_equipment_documents" PRIMARY KEY,
                "EquipmentId" TEXT NOT NULL,
                "DocumentType" TEXT NOT NULL,
                "FileName" TEXT NOT NULL,
                "StoredFileName" TEXT NOT NULL,
                "MimeType" TEXT NOT NULL,
                "FileSize" INTEGER NOT NULL,
                "IssueDate" TEXT NULL,
                "ExpiryDate" TEXT NULL,
                "DocumentNumber" TEXT NULL,
                "Issuer" TEXT NULL,
                "Remarks" TEXT NULL,
                "IsCurrent" INTEGER NOT NULL,
                "CreatedUtc" TEXT NOT NULL,
                "ArchivedUtc" TEXT NULL,
                CONSTRAINT "FK_equipment_documents_equipment_EquipmentId"
                    FOREIGN KEY ("EquipmentId") REFERENCES "equipment" ("Id")
                    ON DELETE CASCADE
            );
            """);

        migrationBuilder.Sql(
            """
            CREATE INDEX IF NOT EXISTS
                "IX_equipment_documents_EquipmentId_DocumentType_CreatedUtc"
            ON "equipment_documents"
                ("EquipmentId", "DocumentType", "CreatedUtc");
            """);

        migrationBuilder.Sql(
            """
            CREATE UNIQUE INDEX IF NOT EXISTS
                "IX_equipment_documents_EquipmentId_DocumentType_Current"
            ON "equipment_documents" ("EquipmentId", "DocumentType")
            WHERE "IsCurrent" = 1;
            """);

        migrationBuilder.Sql(
            $"""
            INSERT INTO "equipment_documents" (
                "Id", "EquipmentId", "DocumentType", "FileName",
                "StoredFileName", "MimeType", "FileSize", "IssueDate",
                "ExpiryDate", "DocumentNumber", "Issuer", "Remarks",
                "IsCurrent", "CreatedUtc", "ArchivedUtc")
            SELECT
                lower(hex(randomblob(4)) || '-' || hex(randomblob(2)) || '-' ||
                    '4' || substr(hex(randomblob(2)), 2) || '-' ||
                    substr('89ab', abs(random()) % 4 + 1, 1) ||
                    substr(hex(randomblob(2)), 2) || '-' || hex(randomblob(6))),
                e."Id", 'Certificate',
                coalesce(e."CurrentCertificateFileName", 'certificate.pdf'),
                e."CurrentCertificatePath", 'application/pdf', 0, NULL,
                e."ExpiryDate", NULL, NULL, e."Remarks", 1,
                CURRENT_TIMESTAMP, NULL
            FROM "equipment" e
            WHERE e."CurrentCertificatePath" IS NOT NULL
              AND trim(e."CurrentCertificatePath") <> ''
              AND NOT EXISTS (
                  SELECT 1 FROM "equipment_documents" d
                  WHERE d."EquipmentId" = e."Id"
                    AND d."DocumentType" = 'Certificate'
                    AND d."StoredFileName" = e."CurrentCertificatePath"
              );
            """);

        migrationBuilder.Sql(
            $"""
            INSERT INTO "equipment_documents" (
                "Id", "EquipmentId", "DocumentType", "FileName",
                "StoredFileName", "MimeType", "FileSize", "IssueDate",
                "ExpiryDate", "DocumentNumber", "Issuer", "Remarks",
                "IsCurrent", "CreatedUtc", "ArchivedUtc")
            SELECT
                lower(hex(randomblob(4)) || '-' || hex(randomblob(2)) || '-' ||
                    '4' || substr(hex(randomblob(2)), 2) || '-' ||
                    substr('89ab', abs(random()) % 4 + 1, 1) ||
                    substr(hex(randomblob(2)), 2) || '-' || hex(randomblob(6))),
                e."Id", 'ServiceReport',
                coalesce(e."CurrentReportFileName", 'report.pdf'),
                e."CurrentReportPath", 'application/pdf', 0, NULL,
                e."ExpiryDate", NULL, NULL, e."Remarks", 1,
                CURRENT_TIMESTAMP, NULL
            FROM "equipment" e
            WHERE e."CurrentReportPath" IS NOT NULL
              AND trim(e."CurrentReportPath") <> ''
              AND NOT EXISTS (
                  SELECT 1 FROM "equipment_documents" d
                  WHERE d."EquipmentId" = e."Id"
                    AND d."DocumentType" = 'ServiceReport'
                    AND d."StoredFileName" = e."CurrentReportPath"
              );
            """);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql(
            """DROP TABLE IF EXISTS "equipment_documents";""");
    }
}
