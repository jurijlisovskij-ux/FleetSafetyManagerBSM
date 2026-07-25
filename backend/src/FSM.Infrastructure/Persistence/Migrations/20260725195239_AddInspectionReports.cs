using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FSM.Infrastructure.Persistence.Migrations;

public partial class AddInspectionReports : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql(
            """
            CREATE TABLE IF NOT EXISTS "inspection_reports" (
              "Id" TEXT NOT NULL CONSTRAINT "PK_inspection_reports" PRIMARY KEY,
              "VesselId" TEXT NOT NULL, "ReportNumber" TEXT NOT NULL,
              "InspectionDate" TEXT NOT NULL, "InspectorName" TEXT NOT NULL,
              "InspectionType" TEXT NOT NULL, "GeneralRemarks" TEXT NULL,
              "State" TEXT NOT NULL, "CreatedUtc" TEXT NOT NULL,
              "UpdatedUtc" TEXT NOT NULL, "ReopenedUtc" TEXT NULL,
              "ReopenedBy" TEXT NULL, "ReopeningReason" TEXT NULL,
              CONSTRAINT "FK_inspection_reports_vessels_VesselId"
                FOREIGN KEY ("VesselId") REFERENCES "vessels" ("Id") ON DELETE RESTRICT
            );
            CREATE UNIQUE INDEX IF NOT EXISTS "IX_inspection_reports_ReportNumber"
              ON "inspection_reports" ("ReportNumber");
            CREATE INDEX IF NOT EXISTS "IX_inspection_reports_VesselId_InspectionDate"
              ON "inspection_reports" ("VesselId", "InspectionDate");

            CREATE TABLE IF NOT EXISTS "inspection_findings" (
              "Id" TEXT NOT NULL CONSTRAINT "PK_inspection_findings" PRIMARY KEY,
              "InspectionReportId" TEXT NOT NULL, "SequenceNumber" INTEGER NOT NULL,
              "Department" TEXT NOT NULL, "EquipmentOrArea" TEXT NOT NULL,
              "ExactLocation" TEXT NULL, "Description" TEXT NOT NULL,
              "Priority" TEXT NOT NULL, "Status" TEXT NOT NULL,
              "InspectorRemarks" TEXT NULL, "CreatedUtc" TEXT NOT NULL,
              "UpdatedUtc" TEXT NOT NULL,
              CONSTRAINT "FK_inspection_findings_inspection_reports_InspectionReportId"
                FOREIGN KEY ("InspectionReportId") REFERENCES "inspection_reports" ("Id")
                ON DELETE CASCADE
            );
            CREATE UNIQUE INDEX IF NOT EXISTS
              "IX_inspection_findings_InspectionReportId_SequenceNumber"
              ON "inspection_findings" ("InspectionReportId", "SequenceNumber");

            CREATE TABLE IF NOT EXISTS "inspection_photos" (
              "Id" TEXT NOT NULL CONSTRAINT "PK_inspection_photos" PRIMARY KEY,
              "FindingId" TEXT NOT NULL, "OriginalFileName" TEXT NOT NULL,
              "StoredFileName" TEXT NOT NULL, "MimeType" TEXT NOT NULL,
              "FileSize" INTEGER NOT NULL, "Caption" TEXT NULL,
              "CreatedUtc" TEXT NOT NULL,
              CONSTRAINT "FK_inspection_photos_inspection_findings_FindingId"
                FOREIGN KEY ("FindingId") REFERENCES "inspection_findings" ("Id")
                ON DELETE CASCADE
            );
            CREATE INDEX IF NOT EXISTS "IX_inspection_photos_FindingId"
              ON "inspection_photos" ("FindingId");

            CREATE TABLE IF NOT EXISTS "inspection_report_pdfs" (
              "Id" TEXT NOT NULL CONSTRAINT "PK_inspection_report_pdfs" PRIMARY KEY,
              "InspectionReportId" TEXT NOT NULL, "VersionNumber" INTEGER NOT NULL,
              "FileName" TEXT NOT NULL, "StoredFileName" TEXT NOT NULL,
              "MimeType" TEXT NOT NULL, "FileSize" INTEGER NOT NULL,
              "GeneratedUtc" TEXT NOT NULL, "GeneratedBy" TEXT NOT NULL,
              "IsCurrent" INTEGER NOT NULL,
              CONSTRAINT "FK_inspection_report_pdfs_inspection_reports_InspectionReportId"
                FOREIGN KEY ("InspectionReportId") REFERENCES "inspection_reports" ("Id")
                ON DELETE CASCADE
            );
            CREATE UNIQUE INDEX IF NOT EXISTS
              "IX_inspection_report_pdfs_InspectionReportId_VersionNumber"
              ON "inspection_report_pdfs" ("InspectionReportId", "VersionNumber");
            CREATE UNIQUE INDEX IF NOT EXISTS "IX_inspection_report_pdfs_Report_Current"
              ON "inspection_report_pdfs" ("InspectionReportId") WHERE "IsCurrent" = 1;
            """);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(name: "inspection_photos");
        migrationBuilder.DropTable(name: "inspection_report_pdfs");
        migrationBuilder.DropTable(name: "inspection_findings");
        migrationBuilder.DropTable(name: "inspection_reports");
    }
}
