using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable
namespace FSM.Infrastructure.Persistence.Migrations;

public partial class AddManagedServiceRequests : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder) =>
        migrationBuilder.Sql(
        """
        CREATE TABLE IF NOT EXISTS "service_requests" (
          "Id" TEXT NOT NULL CONSTRAINT "PK_service_requests" PRIMARY KEY,
          "RequestNumber" TEXT NOT NULL, "VesselId" TEXT NOT NULL,
          "CreatedByUserId" TEXT NOT NULL, "RequestDate" TEXT NOT NULL,
          "RequestedBy" TEXT NOT NULL, "ContactName" TEXT NOT NULL,
          "ContactEmail" TEXT NOT NULL, "ContactPhone" TEXT NULL,
          "PreferredPort" TEXT NULL, "ExpectedArrivalDate" TEXT NULL,
          "ExpectedDepartureDate" TEXT NULL, "ServiceCompany" TEXT NULL,
          "AttentionTo" TEXT NULL, "Subject" TEXT NOT NULL,
          "GeneralRemarks" TEXT NULL, "InvoicingDetails" TEXT NULL,
          "Status" TEXT NOT NULL, "CreatedUtc" TEXT NOT NULL,
          "UpdatedUtc" TEXT NOT NULL, "SentUtc" TEXT NULL,
          "CompletedUtc" TEXT NULL, "CancelledUtc" TEXT NULL,
          "CancellationReason" TEXT NULL, "ReopenedUtc" TEXT NULL,
          "ReopenedBy" TEXT NULL, "ReopenReason" TEXT NULL,
          CONSTRAINT "FK_service_requests_vessels_VesselId"
            FOREIGN KEY ("VesselId") REFERENCES "vessels" ("Id") ON DELETE RESTRICT
        );
        CREATE UNIQUE INDEX IF NOT EXISTS "IX_service_requests_RequestNumber"
          ON "service_requests" ("RequestNumber");
        CREATE INDEX IF NOT EXISTS "IX_service_requests_VesselId_RequestDate"
          ON "service_requests" ("VesselId","RequestDate");

        CREATE TABLE IF NOT EXISTS "service_request_items" (
          "Id" TEXT NOT NULL CONSTRAINT "PK_service_request_items" PRIMARY KEY,
          "ServiceRequestId" TEXT NOT NULL, "SequenceNumber" INTEGER NOT NULL,
          "ItemType" TEXT NOT NULL, "EquipmentId" TEXT NULL,
          "InspectionFindingId" TEXT NULL, "InspectionReportNumber" TEXT NULL,
          "InspectionFindingSequence" INTEGER NULL, "Title" TEXT NOT NULL,
          "Category" TEXT NULL, "Maker" TEXT NULL, "Model" TEXT NULL,
          "SerialNumber" TEXT NULL, "Location" TEXT NULL,
          "CurrentDueDate" TEXT NULL, "Priority" TEXT NULL,
          "Description" TEXT NULL, "ServiceDescription" TEXT NOT NULL,
          "Remarks" TEXT NULL, "CreatedUtc" TEXT NOT NULL, "UpdatedUtc" TEXT NOT NULL,
          FOREIGN KEY ("ServiceRequestId") REFERENCES "service_requests" ("Id") ON DELETE CASCADE,
          FOREIGN KEY ("EquipmentId") REFERENCES "equipment" ("Id") ON DELETE SET NULL,
          FOREIGN KEY ("InspectionFindingId") REFERENCES "inspection_findings" ("Id") ON DELETE SET NULL
        );
        CREATE INDEX IF NOT EXISTS "IX_service_request_items_EquipmentId"
          ON "service_request_items" ("EquipmentId");
        CREATE INDEX IF NOT EXISTS "IX_service_request_items_InspectionFindingId"
          ON "service_request_items" ("InspectionFindingId");
        CREATE UNIQUE INDEX IF NOT EXISTS "IX_service_request_items_ServiceRequestId_SequenceNumber"
          ON "service_request_items" ("ServiceRequestId","SequenceNumber");
        CREATE INDEX IF NOT EXISTS "IX_service_request_items_ServiceRequestId_EquipmentId"
          ON "service_request_items" ("ServiceRequestId","EquipmentId") WHERE "EquipmentId" IS NOT NULL;
        CREATE INDEX IF NOT EXISTS "IX_service_request_items_ServiceRequestId_InspectionFindingId"
          ON "service_request_items" ("ServiceRequestId","InspectionFindingId") WHERE "InspectionFindingId" IS NOT NULL;

        CREATE TABLE IF NOT EXISTS "service_request_attachments" (
          "Id" TEXT NOT NULL CONSTRAINT "PK_service_request_attachments" PRIMARY KEY,
          "ServiceRequestId" TEXT NOT NULL, "OriginalFileName" TEXT NOT NULL,
          "StoredFileName" TEXT NOT NULL, "MimeType" TEXT NOT NULL,
          "FileSize" INTEGER NOT NULL, "Description" TEXT NULL,
          "CreatedUtc" TEXT NOT NULL, "CreatedBy" TEXT NOT NULL,
          FOREIGN KEY ("ServiceRequestId") REFERENCES "service_requests" ("Id") ON DELETE CASCADE
        );
        CREATE INDEX IF NOT EXISTS "IX_service_request_attachments_ServiceRequestId"
          ON "service_request_attachments" ("ServiceRequestId");

        CREATE TABLE IF NOT EXISTS "service_request_pdfs" (
          "Id" TEXT NOT NULL CONSTRAINT "PK_service_request_pdfs" PRIMARY KEY,
          "ServiceRequestId" TEXT NOT NULL, "VersionNumber" INTEGER NOT NULL,
          "FileName" TEXT NOT NULL, "StoredFileName" TEXT NOT NULL,
          "MimeType" TEXT NOT NULL, "FileSize" INTEGER NOT NULL,
          "GeneratedUtc" TEXT NOT NULL, "GeneratedBy" TEXT NOT NULL,
          "IsCurrent" INTEGER NOT NULL,
          FOREIGN KEY ("ServiceRequestId") REFERENCES "service_requests" ("Id") ON DELETE CASCADE
        );
        CREATE UNIQUE INDEX IF NOT EXISTS "IX_service_request_pdfs_ServiceRequestId_VersionNumber"
          ON "service_request_pdfs" ("ServiceRequestId","VersionNumber");
        CREATE UNIQUE INDEX IF NOT EXISTS "IX_service_request_pdfs_Request_Current"
          ON "service_request_pdfs" ("ServiceRequestId") WHERE "IsCurrent" = 1;
        """);

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable("service_request_attachments");
        migrationBuilder.DropTable("service_request_items");
        migrationBuilder.DropTable("service_request_pdfs");
        migrationBuilder.DropTable("service_requests");
    }
}
