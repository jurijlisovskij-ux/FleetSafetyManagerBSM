import React, { useEffect, useMemo, useState } from "react";
import ReactDOM from "react-dom/client";
import "./styles.css";

const apiBaseUrl =
  import.meta.env.VITE_API_BASE_URL ?? "/api";

type LoginResult = {
  token: string;
  userId: string;
  fullName: string;
  email: string;
  role: "OfficeAdmin" | "Superintendent" | "Crew";
};


type ManagedUser = {
  id: string;
  fullName: string;
  email: string;
  role: LoginResult["role"];
  isActive: boolean;
  vesselIds: string[];
};

type Vessel = {
  id: string;
  vesselName: string;
  imoNumber: string;
  flag?: string;
  companyName?: string;
  invoicingDetails?: string;
  accountingEmail?: string;
  displayName: string;
};

type EquipmentCategory =
  | "LSA"
  | "FFE"
  | "Medicine"
  | "Radio & Navigation";

type InspectionBy = "Crew" | "Service Company";
type EquipmentSortField =
  | "equipmentName"
  | "category"
  | "expiryDate"
  | "status";
type SortDirection = "asc" | "desc";
type NavigationView =
  | "dashboard"
  | "equipment"
  | "inspections"
  | "serviceRequests"
  | "medical"
  | "administration"
  | "synchronization"
  | "profile";

type SystemSettings = {
  companyName: string;
  companyLogo: string;
  defaultPdfFooter: string;
  notificationDays: number;
  inspectionDraftWarningDays: number;
  maximumDocumentUploadBytes: number;
  maximumInspectionPhotoBytes: number;
  maximumServiceRequestUploadBytes: number;
  documentStoragePath: string;
  timeZone: string;
  dateFormat: string;
};

type Equipment = {
  id: string;
  vesselId: string;
  category: EquipmentCategory;
  equipmentName: string;
  maker?: string;
  model?: string;
  serialNumber?: string;
  quantity: number;
  inspectionBy: InspectionBy;
  expiryDate?: string;
  status: "OK" | "Soon" | "Overdue";
  remarks?: string;
  reportAvailable: boolean;
  reportFileName?: string;
  certificateAvailable: boolean;
  certificateFileName?: string;
};

type EquipmentDocument = {
  id: string;
  type: "Certificate" | "ServiceReport";
  fileName: string;
  issueDate: string | null;
  expiryDate: string | null;
  documentNumber: string | null;
  issuer: string | null;
  remarks: string | null;
  isCurrent: boolean;
  createdUtc: string;
  archivedUtc: string | null;
};


type DashboardVesselRow = {
  id: string;
  vesselName: string;
  imoNumber: string;
  flag?: string;
  equipment: number;
  ok: number;
  soon: number;
  overdue: number;
};

type DashboardUpcoming = {
  id: string;
  vesselId: string;
  vesselName: string;
  imoNumber: string;
  equipmentName: string;
  category: Equipment["category"];
  expiryDate: string;
  status: Equipment["status"];
  daysLeft: number;
};

type DashboardData = {
  vessels: number;
  equipment: number;
  ok: number;
  soon: number;
  overdue: number;
  vesselRows: DashboardVesselRow[];
  upcoming: DashboardUpcoming[];
};
type FleetDashboardData = {
  counts: {
    equipmentTotal: number; equipmentOk: number; equipmentSoon: number;
    equipmentOverdue: number; inspectionDraft: number; inspectionFinalized: number;
    criticalOpen: number; urgentOpen: number; normalOpen: number;
    serviceDraft: number; serviceReady: number; serviceSent: number;
    serviceCompleted: number; serviceCancelled: number;
  };
  vessels: Array<{
    vesselId: string; vesselName: string; imoNumber: string;
    equipmentOverdue: number; criticalFindings: number; openFindings: number;
    draftInspectionReports: number; activeServiceRequests: number;
    lastSynchronizationUtc: string | null; overallStatus: "Green" | "Yellow" | "Red";
  }>;
  charts: Array<{ name: string; values: Array<{ label: string; value: number }> }>;
  notifications: Array<{
    source: string; severity: "Green" | "Yellow" | "Red";
    vesselId: string; vesselName: string; title: string; detail: string; eventUtc: string;
  }>;
  vesselOptions: Array<{ id: string; name: string }>;
  superintendentOptions: Array<{ id: string; name: string }>;
};

const categories: Equipment["category"][] = [
  "LSA",
  "FFE",
  "Medicine",
  "Radio & Navigation",
];

function authFetch(
  url: string,
  token: string,
  init: RequestInit = {},
) {
  const headers = new Headers(init.headers);
  headers.set("Authorization", `Bearer ${token}`);
  return fetch(url, { ...init, headers });
}

function formatDate(value?: string | null) {
  if (!value) return "—";
  return new Intl.DateTimeFormat("en-GB", {
    day: "2-digit",
    month: "short",
    year: "numeric",
  }).format(new Date(`${value}T00:00:00`));
}

function categoryToApi(category: Equipment["category"]) {
  return {
    LSA: 0,
    FFE: 1,
    Medicine: 2,
    "Radio & Navigation": 3,
  }[category];
}

function inspectionToApi(value: Equipment["inspectionBy"]) {
  return value === "Crew" ? 0 : 1;
}

type InspectionReportSummary = {
  id: string; reportNumber: string; vesselId: string; vesselName: string;
  inspectionDate: string; inspectorName: string;
  inspectionType: "Deck" | "Engine" | "DeckAndEngine";
  state: "Draft" | "Finalized"; findingCount: number;
  openFindingCount: number; createdUtc: string;
};
type InspectionPhoto = {
  id: string; originalFileName: string; mimeType: string;
  fileSize: number; caption: string | null; createdUtc: string;
};
type InspectionFinding = {
  id: string; sequenceNumber: number; department: "Deck" | "Engine";
  equipmentOrArea: string; exactLocation: string | null; description: string;
  priority: "Critical" | "Urgent" | "Normal"; status: "Open" | "Closed";
  inspectorRemarks: string | null; photos: InspectionPhoto[];
};
type InspectionReport = InspectionReportSummary & {
  imoNumber: string; generalRemarks: string | null; updatedUtc: string;
  reopenedUtc: string | null; reopenedBy: string | null;
  reopeningReason: string | null; findings: InspectionFinding[];
};
type InspectionPdf = {
  id: string; versionNumber: number; fileName: string; fileSize: number;
  generatedUtc: string; generatedBy: string; isCurrent: boolean;
};
type ManagedRequestSummary = {
  id: string; requestNumber: string; vesselId: string; vesselName: string;
  requestDate: string; subject: string; preferredPort: string | null;
  status: "Draft" | "Ready" | "Sent" | "Completed" | "Cancelled";
  itemCount: number; requestedBy: string; updatedUtc: string;
};
type ManagedRequestItem = {
  id: string; sequenceNumber: number; itemType: "Equipment" | "InspectionFinding" | "Manual";
  equipmentId: string | null; inspectionFindingId: string | null;
  inspectionReportNumber: string | null; inspectionFindingSequence: number | null;
  title: string; category: string | null; maker: string | null; model: string | null;
  serialNumber: string | null; location: string | null; currentDueDate: string | null;
  priority: string | null; description: string | null;
  serviceDescription: string; remarks: string | null;
};
type ManagedAttachment = {
  id: string; originalFileName: string; mimeType: string; fileSize: number;
  description: string | null; createdUtc: string; createdBy: string;
};
type ManagedServiceRequest = ManagedRequestSummary & {
  imoNumber: string; contactName: string; contactEmail: string; contactPhone: string | null;
  expectedArrivalDate: string | null; expectedDepartureDate: string | null;
  serviceCompany: string | null; attentionTo: string | null;
  generalRemarks: string | null; invoicingDetails: string | null;
  createdUtc: string; sentUtc: string | null; completedUtc: string | null;
  cancelledUtc: string | null; items: ManagedRequestItem[];
  attachments: ManagedAttachment[];
};

function formatDateTime(value: string) {
  return new Intl.DateTimeFormat("en-GB", {
    day: "2-digit", month: "short", year: "numeric",
    hour: "2-digit", minute: "2-digit",
  }).format(new Date(value));
}

function App() {
  const [email, setEmail] = useState("");
  const [password, setPassword] = useState("");
  const [user, setUser] = useState<LoginResult | null>(null);
  const [vessels, setVessels] = useState<Vessel[]>([]);
  const [dashboard, setDashboard] = useState<DashboardData | null>(null);
  const [fleetDashboard, setFleetDashboard] =
    useState<FleetDashboardData | null>(null);
  const [fleetVesselFilter, setFleetVesselFilter] = useState("");
  const [fleetSuperintendentFilter, setFleetSuperintendentFilter] = useState("");
  const [fleetStatusFilter, setFleetStatusFilter] = useState("");
  const [fleetCategoryFilter, setFleetCategoryFilter] = useState("");
  const [fleetYearFilter, setFleetYearFilter] = useState("");
  const [fleetSearch, setFleetSearch] = useState("");
  const [dashboardFilter, setDashboardFilter] =
    useState<"All" | "OK" | "Soon" | "Overdue">("All");
  const [currentVessel, setCurrentVessel] = useState<Vessel | null>(null);
  const [equipment, setEquipment] = useState<Equipment[]>([]);
  const [selected, setSelected] = useState<Equipment | null>(null);
  const [selectedForRequest, setSelectedForRequest] =
    useState<Set<string>>(new Set());
  const [showServiceRequest, setShowServiceRequest] =
    useState(false);
  const [showInspectionList, setShowInspectionList] =
    useState(false);
  const [showAddEquipment, setShowAddEquipment] =
    useState(false);
  const [showVesselProfile, setShowVesselProfile] =
    useState(false);
  const [showUserAdministration, setShowUserAdministration] =
    useState(false);
  const [showCopyEquipment, setShowCopyEquipment] =
    useState(false);
  const [equipmentSearch, setEquipmentSearch] = useState("");
  const [equipmentStatusFilter, setEquipmentStatusFilter] =
    useState("All");
  const [equipmentCategoryFilter, setEquipmentCategoryFilter] =
    useState("All");
  const [equipmentResponsibleFilter, setEquipmentResponsibleFilter] =
    useState("All");
  const [equipmentSortField, setEquipmentSortField] =
    useState<EquipmentSortField>("equipmentName");
  const [equipmentSortDirection, setEquipmentSortDirection] =
    useState<SortDirection>("asc");
  const [error, setError] = useState("");
  const [busy, setBusy] = useState(false);
  const [navigationView, setNavigationView] =
    useState<NavigationView>("dashboard");
  const [detailEquipmentId, setDetailEquipmentId] =
    useState<string | null>(null);
  const [sidebarCollapsed, setSidebarCollapsed] = useState(
    () => sessionStorage.getItem("fsm-sidebar-collapsed") === "true",
  );

  useEffect(() => {
    sessionStorage.setItem(
      "fsm-sidebar-collapsed",
      String(sidebarCollapsed),
    );
  }, [sidebarCollapsed]);

  useEffect(() => {
    function handlePopState(event: PopStateEvent) {
      const equipmentId =
        typeof event.state?.equipmentDetailsId === "string"
          ? event.state.equipmentDetailsId
          : null;
      setDetailEquipmentId(equipmentId);
    }

    window.addEventListener("popstate", handlePopState);
    return () => window.removeEventListener("popstate", handlePopState);
  }, []);

  useEffect(() => {
    if (!user) return;
    Promise.all([
      authFetch(`${apiBaseUrl}/vessels`, user.token),
      authFetch(`${apiBaseUrl}/dashboard`, user.token),
    ])
      .then(async ([vesselsResponse, dashboardResponse]) => {
        if (!vesselsResponse.ok)
          throw new Error("Unable to load vessels.");
        if (!dashboardResponse.ok)
          throw new Error("Unable to load dashboard.");

        const vesselItems: Vessel[] = await vesselsResponse.json();
        const dashboardData: DashboardData =
          await dashboardResponse.json();

        setVessels(vesselItems);
        setDashboard(dashboardData);
      })
      .catch(error => setError((error as Error).message));
  }, [user]);

  useEffect(() => {
    if (!user) return;
    const parameters = new URLSearchParams();
    if (fleetVesselFilter) parameters.set("vesselId", fleetVesselFilter);
    if (fleetSuperintendentFilter)
      parameters.set("superintendentId", fleetSuperintendentFilter);
    if (fleetStatusFilter) parameters.set("status", fleetStatusFilter);
    if (fleetCategoryFilter) parameters.set("category", fleetCategoryFilter);
    if (fleetYearFilter) parameters.set("year", fleetYearFilter);
    if (fleetSearch.trim()) parameters.set("search", fleetSearch.trim());
    const suffix = parameters.size ? `?${parameters}` : "";
    authFetch(`${apiBaseUrl}/fleet-dashboard${suffix}`, user.token)
      .then(async response => {
        if (!response.ok) throw new Error("Unable to load fleet dashboard.");
        return response.json();
      })
      .then(setFleetDashboard)
      .catch(reason => setError((reason as Error).message));
  }, [
    user, fleetVesselFilter, fleetSuperintendentFilter, fleetStatusFilter,
    fleetCategoryFilter, fleetYearFilter, fleetSearch,
  ]);

  useEffect(() => {
    if (!user || !currentVessel) return;
    setBusy(true);
    authFetch(
      `${apiBaseUrl}/vessels/${currentVessel.id}/equipment`,
      user.token,
    )
      .then(async response => {
        if (!response.ok) throw new Error("Unable to load equipment.");
        return response.json();
      })
      .then((items: Equipment[]) => {
        setEquipment(items);
        setSelected(items[0] ?? null);
        setSelectedForRequest(new Set());
        setEquipmentSearch("");
        setEquipmentStatusFilter("All");
        setEquipmentCategoryFilter("All");
        setEquipmentResponsibleFilter("All");
      })
      .catch(error => setError((error as Error).message))
      .finally(() => setBusy(false));
  }, [user, currentVessel]);

  const equipmentStatusOptions = useMemo(
    () => [...new Set(equipment.map(item => item.status))].sort(),
    [equipment],
  );

  const equipmentCategoryOptions = useMemo(
    () => [...new Set(equipment.map(item => item.category))].sort(),
    [equipment],
  );

  const equipmentResponsibleOptions = useMemo(
    () => [...new Set(equipment.map(item => item.inspectionBy))].sort(),
    [equipment],
  );

  const visibleEquipment = useMemo(() => {
    const search = equipmentSearch.trim().toLocaleLowerCase();
    const statusRank: Record<Equipment["status"], number> = {
      OK: 0,
      Soon: 1,
      Overdue: 2,
    };

    return equipment
      .filter(item => {
        const searchableValues = [
          item.equipmentName,
          item.category,
          item.maker,
          item.model,
          item.serialNumber,
        ];

        return (
          (!search ||
            searchableValues.some(value =>
              value?.toLocaleLowerCase().includes(search),
            )) &&
          (equipmentStatusFilter === "All" ||
            item.status === equipmentStatusFilter) &&
          (equipmentCategoryFilter === "All" ||
            item.category === equipmentCategoryFilter) &&
          (equipmentResponsibleFilter === "All" ||
            item.inspectionBy === equipmentResponsibleFilter)
        );
      })
      .sort((left, right) => {
        let comparison = 0;

        if (equipmentSortField === "expiryDate") {
          const leftDate = left.expiryDate ?? "9999-12-31";
          const rightDate = right.expiryDate ?? "9999-12-31";
          comparison = leftDate.localeCompare(rightDate);
        } else if (equipmentSortField === "status") {
          comparison = statusRank[left.status] - statusRank[right.status];
        } else {
          comparison = left[equipmentSortField].localeCompare(
            right[equipmentSortField],
          );
        }

        if (comparison === 0)
          comparison = left.equipmentName.localeCompare(right.equipmentName);

        return equipmentSortDirection === "asc"
          ? comparison
          : -comparison;
      });
  }, [
    equipment,
    equipmentSearch,
    equipmentStatusFilter,
    equipmentCategoryFilter,
    equipmentResponsibleFilter,
    equipmentSortField,
    equipmentSortDirection,
  ]);

  const equipmentQueryActive =
    equipmentSearch.trim() !== "" ||
    equipmentStatusFilter !== "All" ||
    equipmentCategoryFilter !== "All" ||
    equipmentResponsibleFilter !== "All";

  const detailEquipment =
    equipment.find(item => item.id === detailEquipmentId) ?? null;

  function sortEquipment(field: EquipmentSortField) {
    if (equipmentSortField === field) {
      setEquipmentSortDirection(current =>
        current === "asc" ? "desc" : "asc",
      );
      return;
    }

    setEquipmentSortField(field);
    setEquipmentSortDirection("asc");
  }

  function sortIndicator(field: EquipmentSortField) {
    if (equipmentSortField !== field) return "";
    return equipmentSortDirection === "asc" ? " ↑" : " ↓";
  }

  function closeWorkspace() {
    if (detailEquipmentId) {
      window.history.replaceState(
        {},
        "",
        `${window.location.pathname}${window.location.search}`,
      );
    }
    setDetailEquipmentId(null);
    setCurrentVessel(null);
    setEquipment([]);
    setSelected(null);
    setSelectedForRequest(new Set());
    setShowServiceRequest(false);
    setShowInspectionList(false);
    setShowAddEquipment(false);
    setShowVesselProfile(false);
    setShowCopyEquipment(false);
  }

  function navigate(view: NavigationView) {
    if (view !== "equipment") closeWorkspace();
    else if (detailEquipmentId) {
      window.history.replaceState(
        {},
        "",
        `${window.location.pathname}${window.location.search}`,
      );
      setDetailEquipmentId(null);
    }
    setShowUserAdministration(false);
    setNavigationView(view);
  }

  function signOut() {
    if (user)
      void authFetch(`${apiBaseUrl}/auth/logout`, user.token, {
        method: "POST",
        keepalive: true,
      });
    window.history.replaceState(
      {},
      "",
      `${window.location.pathname}${window.location.search}`,
    );
    setUser(null);
    setPassword("");
    setVessels([]);
    setDashboard(null);
    setFleetDashboard(null);
    setDashboardFilter("All");
    closeWorkspace();
    setShowUserAdministration(false);
    setNavigationView("dashboard");
  }

  function openEquipmentDetails(item: Equipment) {
    setSelected(item);
    setDetailEquipmentId(item.id);
    window.history.pushState(
      { equipmentDetailsId: item.id },
      "",
      `#equipment/${item.id}`,
    );
  }

  if (!user) {
    return (
      <div className="login-page">
        <form
          className="card"
          onSubmit={async event => {
            event.preventDefault();
            setBusy(true);
            setError("");
            try {
              const response = await fetch(`${apiBaseUrl}/auth/login`, {
                method: "POST",
                headers: { "Content-Type": "application/json" },
                body: JSON.stringify({ email, password }),
              });

              if (!response.ok) {
                const body = await response.json().catch(() => null);
                throw new Error(body?.message ?? "Unable to sign in.");
              }

              setUser(await response.json());
            } catch (error) {
              setError((error as Error).message);
            } finally {
              setPassword("");
              setBusy(false);
            }
          }}
        >
          <h1>Fleet Safety Manager</h1>
          {error && <div className="error">{error}</div>}
          <label>Email<input value={email} onChange={e => setEmail(e.target.value)} /></label>
          <label>Password<input type="password" value={password} onChange={e => setPassword(e.target.value)} /></label>
          <button className="primary" disabled={busy}>
            {busy ? "Signing in..." : "Sign In"}
          </button>
        </form>
      </div>
    );
  }

  return (
    <div className="application">
      <header className="topbar">
        <div><strong>Fleet Safety Manager</strong><span>Version 1.0.0-rc1</span></div>
        <div className="session">
          <span>{user.fullName} · {user.role}</span>
          {currentVessel &&
            <button
              onClick={() => {
                closeWorkspace();
                setNavigationView("dashboard");
              }}
            >
              Dashboard
            </button>}
          {user.role === "OfficeAdmin" &&
            <button
              onClick={() =>
                setShowUserAdministration(true)}
            >
              Users
            </button>}
          <button onClick={() => {
            signOut();
          }}>Sign Out</button>
        </div>
      </header>

      <div className={`application-body ${sidebarCollapsed ? "sidebar-collapsed" : ""}`}>
        <nav className="sidebar" aria-label="Main navigation">
          <button
            className="sidebar-toggle"
            type="button"
            onClick={() => setSidebarCollapsed(value => !value)}
            aria-label={sidebarCollapsed ? "Expand sidebar" : "Collapse sidebar"}
            title={sidebarCollapsed ? "Expand sidebar" : "Collapse sidebar"}
          >
            <span aria-hidden="true">{sidebarCollapsed ? "»" : "«"}</span>
            {!sidebarCollapsed && <span>Collapse</span>}
          </button>
          <SidebarItem icon="⌂" label="Dashboard" active={navigationView === "dashboard" && !currentVessel} collapsed={sidebarCollapsed} onClick={() => navigate("dashboard")} />
          <SidebarItem icon="☷" label="Equipment List" active={navigationView === "equipment" || !!currentVessel} collapsed={sidebarCollapsed} onClick={() => navigate("equipment")} />
          <SidebarItem icon="▣" label="Inspection Reports" active={navigationView === "inspections"} collapsed={sidebarCollapsed} onClick={() => navigate("inspections")} />
          <SidebarItem icon="⌁" label="Service Requests" active={navigationView === "serviceRequests"} collapsed={sidebarCollapsed} onClick={() => navigate("serviceRequests")} />
          <SidebarItem icon="✚" label="Medical Chest" active={navigationView === "medical"} collapsed={sidebarCollapsed} onClick={() => navigate("medical")} />
          {user.role === "OfficeAdmin" &&
            <SidebarItem icon="⚙" label="Administration" active={navigationView === "administration"} collapsed={sidebarCollapsed} onClick={() => navigate("administration")} />}
          <SidebarItem icon="⟳" label="Synchronization" active={navigationView === "synchronization"} collapsed={sidebarCollapsed} onClick={() => navigate("synchronization")} />
          <div className="sidebar-spacer" />
          <SidebarItem icon="●" label="My Profile" active={navigationView === "profile"} collapsed={sidebarCollapsed} onClick={() => navigate("profile")} />
          <SidebarItem icon="↪" label="Sign Out" collapsed={sidebarCollapsed} onClick={signOut} />
        </nav>

        <div className="page-content">
      {navigationView === "dashboard" && !currentVessel ? (
        <main className="dashboard-page">
          <div className="dashboard-heading">
            <div>
              <h1>Dashboard</h1>
              <p className="muted">Fleet equipment status.</p>
            </div>
          </div>

          {error && <div className="error">{error}</div>}

          {fleetDashboard &&
            <FleetOperationsDashboard
              data={fleetDashboard}
              role={user.role}
              vesselFilter={fleetVesselFilter}
              superintendentFilter={fleetSuperintendentFilter}
              statusFilter={fleetStatusFilter}
              categoryFilter={fleetCategoryFilter}
              yearFilter={fleetYearFilter}
              search={fleetSearch}
              onVesselFilter={setFleetVesselFilter}
              onSuperintendentFilter={setFleetSuperintendentFilter}
              onStatusFilter={setFleetStatusFilter}
              onCategoryFilter={setFleetCategoryFilter}
              onYearFilter={setFleetYearFilter}
              onSearch={setFleetSearch}
              onOpenVessel={setFleetVesselFilter}
            />}

          <div className="dashboard-cards">
            <button className="dashboard-card" onClick={() => setDashboardFilter("All")}>
              <span>Vessels</span>
              <strong>{dashboard?.vessels ?? vessels.length}</strong>
            </button>
            <button className="dashboard-card" onClick={() => setDashboardFilter("All")}>
              <span>Equipment</span>
              <strong>{dashboard?.equipment ?? 0}</strong>
            </button>
            <button className="dashboard-card ok-card" onClick={() => setDashboardFilter("OK")}>
              <span>OK</span>
              <strong>{dashboard?.ok ?? 0}</strong>
            </button>
            <button className="dashboard-card soon-card" onClick={() => setDashboardFilter("Soon")}>
              <span>Soon</span>
              <strong>{dashboard?.soon ?? 0}</strong>
            </button>
            <button className="dashboard-card overdue-card" onClick={() => setDashboardFilter("Overdue")}>
              <span>Overdue</span>
              <strong>{dashboard?.overdue ?? 0}</strong>
            </button>
          </div>

          <section className="dashboard-section">
            <div className="dashboard-section-heading">
              <h2>Fleet Status</h2>
              <span className="muted">Click a vessel to open it.</span>
            </div>

            {(dashboard?.vesselRows.length ?? 0) === 0
              ? <div className="empty-state">No vessels assigned.</div>
              : <div className="dashboard-table-wrap">
                  <table className="dashboard-table">
                    <thead>
                      <tr>
                        <th>Vessel</th>
                        <th>Equipment</th>
                        <th>OK</th>
                        <th>Soon</th>
                        <th>Overdue</th>
                      </tr>
                    </thead>
                    <tbody>
                      {(dashboard?.vesselRows ?? []).map(row => (
                        <tr
                          key={row.id}
                          onClick={() => {
                            const vessel = vessels.find(item => item.id === row.id);
                            if (vessel) {
                              setCurrentVessel(vessel);
                              setNavigationView("equipment");
                            }
                          }}
                        >
                          <td>
                            <strong>{row.vesselName}</strong>
                            <small>IMO {row.imoNumber}</small>
                          </td>
                          <td>{row.equipment}</td>
                          <td className="status-ok-text">{row.ok}</td>
                          <td className="status-soon-text">{row.soon}</td>
                          <td className="status-overdue-text">{row.overdue}</td>
                        </tr>
                      ))}
                    </tbody>
                  </table>
                </div>}
          </section>

          <section className="dashboard-section">
            <div className="dashboard-section-heading">
              <h2>Upcoming Inspections</h2>
              <div className="dashboard-filter">
                {(["All", "OK", "Soon", "Overdue"] as const).map(value => (
                  <button
                    key={value}
                    className={dashboardFilter === value ? "selected" : ""}
                    onClick={() => setDashboardFilter(value)}
                  >
                    {value}
                  </button>
                ))}
              </div>
            </div>

            <div className="dashboard-table-wrap">
              <table className="dashboard-table">
                <thead>
                  <tr>
                    <th>Vessel</th>
                    <th>Equipment</th>
                    <th>Category</th>
                    <th>Expiry</th>
                    <th>Days Left</th>
                    <th>Status</th>
                  </tr>
                </thead>
                <tbody>
                  {(dashboard?.upcoming ?? [])
                    .filter(item => dashboardFilter === "All" || item.status === dashboardFilter)
                    .map(item => (
                      <tr
                        key={item.id}
                        onClick={() => {
                          const vessel = vessels.find(value => value.id === item.vesselId);
                          if (vessel) {
                            setCurrentVessel(vessel);
                            setNavigationView("equipment");
                          }
                        }}
                      >
                        <td>{item.vesselName}</td>
                        <td>{item.equipmentName}</td>
                        <td>{item.category}</td>
                        <td>{formatDate(item.expiryDate)}</td>
                        <td>{item.daysLeft}</td>
                        <td><span className={`status ${item.status.toLowerCase()}`}>{item.status}</span></td>
                      </tr>
                    ))}
                </tbody>
              </table>
            </div>
          </section>
        </main>
      ) : navigationView === "inspections" ? (
        <InspectionReportsPage user={user} vessels={vessels} />
      ) : navigationView === "serviceRequests" ? (
        <ManagedServiceRequestsPage user={user} vessels={vessels} />
      ) : navigationView === "equipment" &&
          currentVessel &&
          detailEquipment ? (
        <EquipmentDetailsPage
          item={detailEquipment}
          vessel={currentVessel}
          token={user.token}
          onBack={() => window.history.back()}
        />
      ) : navigationView === "equipment" && currentVessel ? (
        <main className="workspace">
          <div className="workspace-heading">
            <div>
              <button
                className="back-button"
                onClick={() => {
                  closeWorkspace();
                  setNavigationView("dashboard");
                }}
              >
                ← Assigned Vessels
              </button>
              <h1>Equipment List</h1>
              <p className="muted">
                {currentVessel.vesselName} · IMO {currentVessel.imoNumber}
                {currentVessel.flag ? ` · ${currentVessel.flag}` : ""}
              </p>
            </div>
            <div className="workspace-actions">
              <span className="muted">Equipment Register</span>
              <button
                type="button"
                disabled
                title="Synchronization is not configured in this build."
              >
                Synchronization
              </button>
              <button
                onClick={() => setShowVesselProfile(true)}
              >
                Vessel Profile
              </button>
              {user.role === "OfficeAdmin" &&
                <button onClick={() => setShowCopyEquipment(true)}>
                  Copy Equipment List
                </button>}
              <button
                className="primary"
                onClick={() => setShowAddEquipment(true)}
              >
                Add Equipment
              </button>
              <button
                disabled={selectedForRequest.size === 0}
                onClick={() => setShowInspectionList(true)}
              >
                Generate Inspection List
              </button>
              <button
                className="primary"
                disabled={selectedForRequest.size === 0}
                onClick={() => setShowServiceRequest(true)}
              >
                Generate Service Request
              </button>
            </div>
          </div>

          {error && <div className="error">{error}</div>}

          {busy ? (
            <div className="loading">Loading equipment...</div>
          ) : (
            <>
            <section className="equipment-toolbar" aria-label="Equipment filters">
              <label className="equipment-search">
                Search
                <input
                  type="search"
                  value={equipmentSearch}
                  placeholder="Search equipment"
                  onChange={event => setEquipmentSearch(event.target.value)}
                />
              </label>
              <label>
                Status
                <select
                  value={equipmentStatusFilter}
                  onChange={event => setEquipmentStatusFilter(event.target.value)}
                >
                  <option>All</option>
                  {equipmentStatusOptions.map(value =>
                    <option key={value}>{value}</option>)}
                </select>
              </label>
              <label>
                Category
                <select
                  value={equipmentCategoryFilter}
                  onChange={event => setEquipmentCategoryFilter(event.target.value)}
                >
                  <option>All</option>
                  {equipmentCategoryOptions.map(value =>
                    <option key={value}>{value}</option>)}
                </select>
              </label>
              <label>
                Responsible
                <select
                  value={equipmentResponsibleFilter}
                  onChange={event => setEquipmentResponsibleFilter(event.target.value)}
                >
                  <option>All</option>
                  {equipmentResponsibleOptions.map(value =>
                    <option key={value}>{value}</option>)}
                </select>
              </label>
              <span className="equipment-result-count">
                {visibleEquipment.length} of {equipment.length} records
              </span>
            </section>
            <div className="workspace-grid">
              <section className="register">
                <div className="table-scroll">
                  <table className="equipment-table">
                    <thead>
                      <tr>
                        <th className="select-column">Select</th>
                        <th aria-sort={equipmentSortField === "status" ? equipmentSortDirection === "asc" ? "ascending" : "descending" : "none"}>
                          <button className="sort-button" data-sort-field="status" type="button" onClick={() => sortEquipment("status")}>
                            Status{sortIndicator("status")}
                          </button>
                        </th>
                        <th aria-sort={equipmentSortField === "equipmentName" ? equipmentSortDirection === "asc" ? "ascending" : "descending" : "none"}>
                          <button className="sort-button" data-sort-field="equipmentName" type="button" onClick={() => sortEquipment("equipmentName")}>
                            Equipment{sortIndicator("equipmentName")}
                          </button>
                        </th>
                        <th aria-sort={equipmentSortField === "category" ? equipmentSortDirection === "asc" ? "ascending" : "descending" : "none"}>
                          <button className="sort-button" data-sort-field="category" type="button" onClick={() => sortEquipment("category")}>
                            Category{sortIndicator("category")}
                          </button>
                        </th>
                        <th>Manufacturer</th>
                        <th>Model</th>
                        <th>Serial Number</th>
                        <th aria-sort={equipmentSortField === "expiryDate" ? equipmentSortDirection === "asc" ? "ascending" : "descending" : "none"}>
                          <button className="sort-button" data-sort-field="expiryDate" type="button" onClick={() => sortEquipment("expiryDate")}>
                            Due Date{sortIndicator("expiryDate")}
                          </button>
                        </th>
                        <th>Responsible</th>
                        <th>Actions</th>
                      </tr>
                    </thead>
                    <tbody>
                      {visibleEquipment.map(item => (
                              <tr
                                key={item.id}
                                className={
                                  selected?.id === item.id
                                    ? "selected-row"
                                    : ""
                                }
                                onClick={() => setSelected(item)}
                              >
                                <td
                                  className="select-column"
                                  onClick={event => event.stopPropagation()}
                                >
                                  <input
                                    type="checkbox"
                                    checked={selectedForRequest.has(item.id)}
                                    onChange={event => {
                                      setSelectedForRequest(current => {
                                        const next = new Set(current);
                                        event.target.checked
                                          ? next.add(item.id)
                                          : next.delete(item.id);
                                        return next;
                                      });
                                    }}
                                  />
                                </td>
                                <td>
                                  <span className={`status status-${item.status.toLowerCase()}`}>
                                    {item.status.toUpperCase()}
                                  </span>
                                </td>
                                <td>{item.equipmentName}</td>
                                <td>{item.category}</td>
                                <td>{item.maker || "—"}</td>
                                <td>{item.model || "—"}</td>
                                <td>{item.serialNumber || "—"}</td>
                                <td>{formatDate(item.expiryDate)}</td>
                                <td>{item.inspectionBy}</td>
                                <td>
                                  <button
                                    type="button"
                                    className="table-action"
                                    onClick={event => {
                                      event.stopPropagation();
                                      openEquipmentDetails(item);
                                    }}
                                  >
                                    View
                                  </button>
                                </td>
                              </tr>
                      ))}
                      {equipment.length === 0 &&
                        <tr className="table-state-row">
                          <td colSpan={10}>No equipment exists for this vessel.</td>
                        </tr>}
                      {equipment.length > 0 &&
                        visibleEquipment.length === 0 &&
                        <tr className="table-state-row">
                          <td colSpan={10}>
                            {equipmentQueryActive
                              ? "No equipment matches the current search and filters."
                              : "No equipment records to display."}
                          </td>
                        </tr>}
                    </tbody>
                  </table>
                </div>
              </section>

              <aside className="details">
                {selected ? (
                  <EquipmentDetails
                    key={selected.id}
                    item={selected}
                    token={user.token}
                    vesselId={currentVessel.id}
                    onSaved={saved => {
                      setEquipment(current =>
                        current.map(item => item.id === saved.id ? saved : item),
                      );
                      setSelected(saved);
                    }}
                    onDeleted={id => {
                      setEquipment(current => current.filter(item => item.id !== id));
                      setSelectedForRequest(current => {
                        const next = new Set(current);
                        next.delete(id);
                        return next;
                      });
                      const remaining = equipment.filter(item => item.id !== id);
                      setSelected(remaining[0] ?? null);
                    }}
                    onDocumentsChanged={async () => {
                      const response = await authFetch(
                        `${apiBaseUrl}/vessels/${currentVessel.id}/equipment`,
                        user.token,
                      );

                      if (!response.ok)
                        throw new Error("Unable to refresh equipment.");

                      const items: Equipment[] = await response.json();
                      setEquipment(items);
                      setSelected(
                        items.find(x => x.id === selected.id) ?? null,
                      );
                    }}
                  />
                ) : (
                  <div className="empty-details">Select equipment</div>
                )}
              </aside>
            </div>
            </>
          )}
        </main>
      ) : navigationView === "equipment" ? (
        <NavigationPage
          title="Equipment List"
          description="Select a vessel from the Dashboard to open its equipment register."
          actionLabel="Go to Dashboard"
          onAction={() => navigate("dashboard")}
        />
      ) : navigationView === "medical" ? (
        <NavigationPage
          title="Medical Chest"
          description="Medical chest management is not configured in this build."
        />
      ) : navigationView === "synchronization" ? (
        <NavigationPage
          title="Synchronization"
          description="Synchronization is not configured in this build."
        />
      ) : navigationView === "profile" ? (
        <main className="navigation-page">
          <section className="navigation-card profile-card">
            <h1>My Profile</h1>
            <div className="details-grid">
              <Detail label="Full Name" value={user.fullName} />
              <Detail label="Email" value={user.email} />
              <Detail label="Role" value={user.role} />
            </div>
          </section>
        </main>
      ) : (
        <AdministrationCenter token={user.token} />
      )}
        </div>
      </div>






      {showCopyEquipment &&
        currentVessel &&
        user.role === "OfficeAdmin" &&
        <CopyEquipmentDialog
          token={user.token}
          targetVessel={currentVessel}
          vessels={vessels}
          onClose={() => setShowCopyEquipment(false)}
          onCompleted={async () => {
            const response = await authFetch(
              `${apiBaseUrl}/vessels/${currentVessel.id}/equipment`,
              user.token,
            );
            if (!response.ok)
              throw new Error("Unable to refresh equipment.");

            const items: Equipment[] = await response.json();
            setEquipment(items);
            setSelected(items[0] ?? null);
            setSelectedForRequest(new Set());
            setShowCopyEquipment(false);
          }}
        />}

      {showUserAdministration &&
        user.role === "OfficeAdmin" &&
        <UserAdministrationDialog
          token={user.token}
          vessels={vessels}
          onClose={() => {
            setShowUserAdministration(false);
            setNavigationView("dashboard");
          }}
        />}

      {showVesselProfile &&
        currentVessel &&
        <VesselProfileDialog
          token={user.token}
          userRole={user.role}
          vessel={currentVessel}
          onClose={() => setShowVesselProfile(false)}
          onSaved={updated => {
            setVessels(current =>
              current.map(item =>
                item.id === updated.id
                  ? updated
                  : item,
              ),
            );
            setCurrentVessel(updated);
            setShowVesselProfile(false);
            setShowUserAdministration(false);
            setShowCopyEquipment(false);
          }}
        />}

      {showAddEquipment &&
        currentVessel &&
        <AddEquipmentDialog
          token={user.token}
          vesselId={currentVessel.id}
          onClose={() => setShowAddEquipment(false)}
          onCreated={item => {
            setEquipment(current =>
              [...current, item].sort((a, b) => {
                const categoryCompare =
                  a.category.localeCompare(b.category);

                if (categoryCompare !== 0)
                  return categoryCompare;

                return a.equipmentName.localeCompare(
                  b.equipmentName,
                );
              }),
            );
            setSelected(item);
            setShowAddEquipment(false);
            setShowVesselProfile(false);
            setShowUserAdministration(false);
            setShowCopyEquipment(false);
          }}
        />}

      {showInspectionList &&
        currentVessel &&
        <InspectionListDialog
          token={user.token}
          vessel={currentVessel}
          equipment={equipment}
          selectedIds={selectedForRequest}
          onClose={() => setShowInspectionList(false)}
          onGenerated={() => {
            setShowInspectionList(false);
            setShowAddEquipment(false);
            setShowVesselProfile(false);
            setShowUserAdministration(false);
            setShowCopyEquipment(false);
            setSelectedForRequest(new Set());
          }}
        />}

      {showServiceRequest &&
        currentVessel &&
        <ServiceRequestDialog
          token={user.token}
          vessel={currentVessel}
          equipment={equipment}
          selectedIds={selectedForRequest}
          onClose={() => setShowServiceRequest(false)}
          onGenerated={() => {
            setShowServiceRequest(false);
            setShowInspectionList(false);
            setShowAddEquipment(false);
            setShowVesselProfile(false);
            setShowUserAdministration(false);
            setShowCopyEquipment(false);
            setSelectedForRequest(new Set());
          }}
        />}

    </div>
  );
}

function SidebarItem({
  icon,
  label,
  active = false,
  collapsed,
  onClick,
}: {
  icon: string;
  label: string;
  active?: boolean;
  collapsed: boolean;
  onClick: () => void;
}) {
  return (
    <button
      type="button"
      className={`sidebar-item ${active ? "active" : ""}`}
      onClick={onClick}
      title={collapsed ? label : undefined}
      aria-current={active ? "page" : undefined}
    >
      <span className="sidebar-icon" aria-hidden="true">{icon}</span>
      {!collapsed && <span>{label}</span>}
    </button>
  );
}

function NavigationPage({
  title,
  description,
  actionLabel,
  onAction,
}: {
  title: string;
  description: string;
  actionLabel?: string;
  onAction?: () => void;
}) {
  return (
    <main className="navigation-page">
      <section className="navigation-card">
        <h1>{title}</h1>
        <p className="muted">{description}</p>
        {actionLabel && onAction &&
          <button className="primary" type="button" onClick={onAction}>
            {actionLabel}
          </button>}
      </section>
    </main>
  );
}

function EquipmentDetailsPage({
  item,
  vessel,
  token,
  onBack,
}: {
  item: Equipment;
  vessel: Vessel;
  token: string;
  onBack: () => void;
}) {
  const [documents, setDocuments] = useState<EquipmentDocument[]>([]);
  const [documentsBusy, setDocumentsBusy] = useState(true);
  const [documentsError, setDocumentsError] = useState("");

  useEffect(() => {
    const controller = new AbortController();

    setDocumentsBusy(true);
    setDocumentsError("");
    authFetch(
      `${apiBaseUrl}/equipment/${item.id}/documents`,
      token,
      { signal: controller.signal },
    )
      .then(async response => {
        if (!response.ok)
          throw new Error("Unable to load equipment documents.");
        return response.json();
      })
      .then((items: EquipmentDocument[]) => setDocuments(items))
      .catch(exception => {
        if ((exception as Error).name !== "AbortError")
          setDocumentsError((exception as Error).message);
      })
      .finally(() => {
        if (!controller.signal.aborted)
          setDocumentsBusy(false);
      });

    return () => controller.abort();
  }, [item.id, token]);

  const [certificates, reports] = useMemo(() => [
    documents.filter(document => document.type === "Certificate"),
    documents.filter(document => document.type === "ServiceReport"),
  ], [documents]);

  return (
    <main className="equipment-details-page">
      <button className="back-button" type="button" onClick={onBack}>
        ← Back to Equipment List
      </button>
      <div className="equipment-details-heading">
        <div>
          <h1>Equipment Details</h1>
          <p className="muted">
            {vessel.vesselName} · IMO {vessel.imoNumber}
          </p>
        </div>
        <span className={`status status-${item.status.toLowerCase()}`}>
          {item.status.toUpperCase()}
        </span>
      </div>

      <div className="equipment-details-columns">
        <section className="equipment-details-card">
          <h2>Equipment Information</h2>
          <div className="details-grid">
            <Detail label="Equipment Name" value={item.equipmentName} />
            <Detail label="Category" value={item.category} />
            <Detail label="Manufacturer" value={item.maker || "—"} />
            <Detail label="Model" value={item.model || "—"} />
            <Detail label="Serial Number" value={item.serialNumber || "—"} />
            <Detail label="Responsible" value={item.inspectionBy} />
            <Detail label="Current Status" value={item.status.toUpperCase()} />
            <Detail label="Expiry Date" value={formatDate(item.expiryDate)} />
          </div>
        </section>

        <section className="equipment-details-card">
          <h2>Equipment Notes</h2>
          <p className="equipment-notes">
            {item.remarks || "No equipment notes."}
          </p>
        </section>
      </div>

      <div className="equipment-related-sections">
        <section className="equipment-details-card">
          <h2>Certificates</h2>
          <DocumentRepositorySection
            documents={certificates}
            token={token}
            typeLabel="certificate"
            currentEmpty="No certificate available"
            archiveEmpty="No archived certificates"
            busy={documentsBusy}
            error={documentsError}
          />
        </section>

        <section className="equipment-details-card">
          <h2>Service Reports</h2>
          <DocumentRepositorySection
            documents={reports}
            token={token}
            typeLabel="service report"
            currentEmpty="No service report available"
            archiveEmpty="No archived service reports"
            busy={documentsBusy}
            error={documentsError}
          />
        </section>

        <section className="equipment-details-card">
          <h2>History</h2>
          <p className="muted">No history available</p>
        </section>
      </div>
    </main>
  );
}

function DocumentRepositorySection({
  documents,
  token,
  typeLabel,
  currentEmpty,
  archiveEmpty,
  busy,
  error,
}: {
  documents: EquipmentDocument[];
  token: string;
  typeLabel: string;
  currentEmpty: string;
  archiveEmpty: string;
  busy: boolean;
  error: string;
}) {
  if (busy) return <p className="muted">Loading documents...</p>;
  if (error) return <p className="document-error">{error}</p>;

  const current = documents.filter(document => document.isCurrent);
  const archive = documents.filter(document => !document.isCurrent);

  return (
    <div className="document-repository">
      <h3>Current</h3>
      {current.length === 0
        ? <p className="muted">{currentEmpty}</p>
        : current.map(document =>
            <DocumentRepositoryRow
              key={document.id}
              document={document}
              token={token}
              label={typeLabel}
            />)}

      <h3>Archive</h3>
      {archive.length === 0
        ? <p className="muted">{archiveEmpty}</p>
        : archive.map(document =>
            <DocumentRepositoryRow
              key={document.id}
              document={document}
              token={token}
              label={typeLabel}
            />)}
    </div>
  );
}

function DocumentRepositoryRow({
  document,
  token,
  label,
}: {
  document: EquipmentDocument;
  token: string;
  label: string;
}) {
  return (
    <article className="document-repository-row">
      <div className="document-summary-heading">
        <strong>{document.fileName}</strong>
        <DocumentOpenButton
          token={token}
          documentId={document.id}
          label={label}
        />
      </div>
      <div className="document-badges">
        <span className={`document-badge ${
          document.isCurrent ? "current" : "archived"
        }`}>
          {document.isCurrent ? "Current" : "Archive"}
        </span>
      </div>
      <dl>
        <dt>Issue Date</dt>
        <dd>{formatDate(document.issueDate)}</dd>
        <dt>Expiry Date</dt>
        <dd>{formatDate(document.expiryDate)}</dd>
        <dt>Document Number</dt>
        <dd>{document.documentNumber || "—"}</dd>
        <dt>Issuer</dt>
        <dd>{document.issuer || "—"}</dd>
        <dt>Remarks</dt>
        <dd>{document.remarks || "—"}</dd>
        <dt>Created</dt>
        <dd>{new Intl.DateTimeFormat("en-GB", {
          dateStyle: "medium",
          timeStyle: "short",
        }).format(new Date(document.createdUtc))}</dd>
      </dl>
    </article>
  );
}

function DocumentOpenButton({
  token,
  documentId,
  label,
}: {
  token: string;
  documentId: string;
  label: string;
}) {
  const [busy, setBusy] = useState(false);
  const [openError, setOpenError] = useState("");

  async function openDocument() {
    setBusy(true);
    setOpenError("");

    try {
      const response = await authFetch(
        `${apiBaseUrl}/documents/${documentId}/download`,
        token,
      );

      if (!response.ok)
        throw new Error(`Unable to open ${label}.`);

      const blob = await response.blob();
      const url = URL.createObjectURL(blob);
      window.open(url, "_blank", "noopener");
      window.setTimeout(() => URL.revokeObjectURL(url), 60_000);
    } catch (exception) {
      setOpenError((exception as Error).message);
    } finally {
      setBusy(false);
    }
  }

  return (
    <div className="document-open-action">
      <button type="button" disabled={busy} onClick={openDocument}>
        {busy ? "Opening..." : "Open"}
      </button>
      {openError && <small className="document-error">{openError}</small>}
    </div>
  );
}

type CopyMode = "AddOnlyNew" | "ReplaceExisting";

function CopyEquipmentDialog({
  token,
  targetVessel,
  vessels,
  onClose,
  onCompleted,
}: {
  token: string;
  targetVessel: Vessel;
  vessels: Vessel[];
  onClose: () => void;
  onCompleted: () => Promise<void>;
}) {
  const sourceOptions = vessels.filter(
    vessel => vessel.id !== targetVessel.id,
  );

  const [sourceVesselId, setSourceVesselId] =
    useState(sourceOptions[0]?.id ?? "");
  const [mode, setMode] =
    useState<CopyMode>("AddOnlyNew");
  const [copyBusy, setCopyBusy] = useState(false);
  const [copyError, setCopyError] = useState("");
  const [copyResult, setCopyResult] = useState<{
    sourceCount: number;
    added: number;
    skipped: number;
    removed: number;
  } | null>(null);

  async function copyEquipment() {
    if (!sourceVesselId) {
      setCopyError("Select a source vessel.");
      return;
    }

    const message =
      mode === "ReplaceExisting"
        ? "Delete all target equipment and documents, then copy the source list?"
        : "Copy new equipment from the selected vessel?";

    if (!window.confirm(message))
      return;

    setCopyBusy(true);
    setCopyError("");

    try {
      const response = await authFetch(
        `${apiBaseUrl}/vessels/${targetVessel.id}/copy-equipment`,
        token,
        {
          method: "POST",
          headers: { "Content-Type": "application/json" },
          body: JSON.stringify({
            sourceVesselId,
            mode: mode === "AddOnlyNew" ? 0 : 1,
          }),
        },
      );

      if (!response.ok) {
        const body = await response.json().catch(() => null);
        throw new Error(body?.message ?? "Unable to copy equipment.");
      }

      setCopyResult(await response.json());
    } catch (exception) {
      setCopyError((exception as Error).message);
    } finally {
      setCopyBusy(false);
    }
  }

  return (
    <div className="modal">
      <div className="copy-equipment-dialog">
        <div className="service-request-header">
          <div>
            <h2>Copy Equipment List</h2>
            <p>
              Target: {targetVessel.vesselName}
              {" · "}IMO {targetVessel.imoNumber}
            </p>
          </div>
          <button type="button" onClick={onClose}>
            Close
          </button>
        </div>

        {copyError && <div className="error">{copyError}</div>}

        {copyResult
          ? (
            <>
              <div className="copy-result">
                <h3>Equipment copied</h3>
                <dl>
                  <dt>Source positions</dt>
                  <dd>{copyResult.sourceCount}</dd>
                  <dt>Added</dt>
                  <dd>{copyResult.added}</dd>
                  <dt>Skipped</dt>
                  <dd>{copyResult.skipped}</dd>
                  <dt>Removed</dt>
                  <dd>{copyResult.removed}</dd>
                </dl>
              </div>
              <div className="service-request-actions">
                <button className="primary" onClick={onCompleted}>
                  Done
                </button>
              </div>
            </>
          )
          : (
            <>
              {sourceOptions.length === 0
                ? (
                  <div className="empty-state">
                    Add another vessel before copying.
                  </div>
                )
                : (
                  <div className="copy-equipment-form">
                    <label>
                      Source Vessel *
                      <select
                        value={sourceVesselId}
                        onChange={event =>
                          setSourceVesselId(event.target.value)}
                      >
                        {sourceOptions.map(vessel => (
                          <option key={vessel.id} value={vessel.id}>
                            {vessel.vesselName}
                            {" · "}IMO {vessel.imoNumber}
                          </option>
                        ))}
                      </select>
                    </label>

                    <fieldset>
                      <legend>Copy mode</legend>

                      <label className="attachment-option">
                        <input
                          type="radio"
                          name="copy-mode"
                          checked={mode === "AddOnlyNew"}
                          onChange={() => setMode("AddOnlyNew")}
                        />
                        Add only new equipment
                      </label>

                      <label className="attachment-option">
                        <input
                          type="radio"
                          name="copy-mode"
                          checked={mode === "ReplaceExisting"}
                          onChange={() => setMode("ReplaceExisting")}
                        />
                        Replace existing list
                      </label>
                    </fieldset>

                    <div className="copy-fields-note">
                      <strong>Copied:</strong> Category, Equipment Name,
                      Maker, Model and Inspection By.
                      <br />
                      <strong>Not copied:</strong> Quantity, Serial Number,
                      Expiry Date, Remarks, Reports and Certificates.
                    </div>
                  </div>
                )}

              <div className="service-request-actions">
                <button type="button" onClick={onClose}>
                  Cancel
                </button>
                <button
                  type="button"
                  className="primary"
                  disabled={copyBusy || sourceOptions.length === 0}
                  onClick={copyEquipment}
                >
                  {copyBusy ? "Copying..." : "Copy Equipment"}
                </button>
              </div>
            </>
          )}
      </div>
    </div>
  );
}

function AdministrationCenter({ token }: { token: string }) {
  const [tab, setTab] = useState<
    "settings" | "backups" | "health" | "audit" | "maintenance" |
    "diagnostics" | "exports"
  >("settings");
  const [settings, setSettings] = useState<SystemSettings | null>(null);
  const [backups, setBackups] = useState<any[]>([]);
  const [health, setHealth] = useState<any>(null);
  const [diagnostics, setDiagnostics] = useState<any>(null);
  const [auditItems, setAuditItems] = useState<any[]>([]);
  const [message, setMessage] = useState("");
  const [busy, setBusy] = useState(false);

  async function read(path: string) {
    const response = await authFetch(`${apiBaseUrl}/administration/${path}`, token);
    if (!response.ok) throw new Error(await administrationResponseMessage(response));
    return response.json();
  }

  async function refresh() {
    setBusy(true);
    setMessage("");
    try {
      if (tab === "settings") setSettings(await read("settings"));
      if (tab === "backups") setBackups(await read("backups"));
      if (tab === "health") setHealth(await read("health"));
      if (tab === "diagnostics") setDiagnostics(await read("diagnostics"));
      if (tab === "audit") {
        const result = await read("audit?pageSize=200");
        setAuditItems(result.items);
      }
    } catch (error) {
      setMessage(error instanceof Error ? error.message : "Administration request failed.");
    } finally {
      setBusy(false);
    }
  }

  useEffect(() => { void refresh(); }, [tab]);

  async function saveSettings(event: React.FormEvent) {
    event.preventDefault();
    if (!settings) return;
    setBusy(true); setMessage("");
    try {
      const response = await authFetch(`${apiBaseUrl}/administration/settings`, token, {
        method: "PUT",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify(settings),
      });
      if (!response.ok) throw new Error(await administrationResponseMessage(response));
      setMessage("Settings saved. Runtime service changes apply after restart.");
    } catch (error) {
      setMessage(error instanceof Error ? error.message : "Unable to save settings.");
    } finally { setBusy(false); }
  }

  async function createBackup(type: string) {
    setBusy(true); setMessage("");
    try {
      const response = await authFetch(
        `${apiBaseUrl}/administration/backups/${type}`, token, { method: "POST" });
      if (!response.ok) throw new Error(await administrationResponseMessage(response));
      setMessage(`${type} backup created.`);
      setBackups(await read("backups"));
    } catch (error) {
      setMessage(error instanceof Error ? error.message : "Backup failed.");
    } finally { setBusy(false); }
  }

  async function restoreBackup(id: string) {
    if (!window.confirm("Validate this backup for offline restore?")) return;
    setBusy(true); setMessage("");
    try {
      const response = await authFetch(
        `${apiBaseUrl}/administration/backups/${id}/restore`, token, { method: "POST" });
      const result = await response.json();
      if (!response.ok) throw new Error(result.detail ?? result.title ?? "Restore validation failed.");
      setMessage(result.message);
    } catch (error) {
      setMessage(error instanceof Error ? error.message : "Restore validation failed.");
    } finally { setBusy(false); }
  }

  async function maintenance(operation: string) {
    setBusy(true); setMessage("");
    try {
      const response = await authFetch(
        `${apiBaseUrl}/administration/maintenance/${operation}`, token, { method: "POST" });
      const result = await response.json();
      if (!response.ok) throw new Error(result.detail ?? result.title ?? "Maintenance failed.");
      setMessage(result.details);
    } catch (error) {
      setMessage(error instanceof Error ? error.message : "Maintenance failed.");
    } finally { setBusy(false); }
  }

  async function downloadExport(name: string, format: "csv" | "xlsx") {
    setBusy(true); setMessage("");
    try {
      const response = await authFetch(
        `${apiBaseUrl}/administration/exports/${name}.${format}`, token);
      if (!response.ok) throw new Error(await administrationResponseMessage(response));
      const url = URL.createObjectURL(await response.blob());
      const anchor = document.createElement("a");
      anchor.href = url;
      anchor.download = response.headers.get("content-disposition")
        ?.match(/filename="?([^";]+)"?/)?.[1] ?? `${name}.${format}`;
      anchor.click(); URL.revokeObjectURL(url);
    } catch (error) {
      setMessage(error instanceof Error ? error.message : "Export failed.");
    } finally { setBusy(false); }
  }

  const tabs = [
    ["settings", "Settings"], ["backups", "Backups"], ["health", "Health"],
    ["audit", "Audit Log"], ["maintenance", "Maintenance"],
    ["diagnostics", "Diagnostics"], ["exports", "Export"],
  ] as const;

  return <main className="navigation-page administration-center">
    <section className="navigation-card">
      <div className="section-heading">
        <div><h1>Administration</h1><p>Deployment, backup and system management.</p></div>
        <button type="button" className="secondary" onClick={() => void refresh()}
          disabled={busy}>Refresh</button>
      </div>
      <nav className="administration-tabs" aria-label="Administration sections">
        {tabs.map(([value, label]) =>
          <button key={value} type="button"
            className={tab === value ? "active" : ""}
            onClick={() => setTab(value)}>{label}</button>)}
      </nav>
      {message && <div className="inline-message">{message}</div>}
      {busy && <p className="muted">Working…</p>}

      {tab === "settings" && settings &&
        <form className="administration-form" onSubmit={saveSettings}>
          <label>Company Name<input value={settings.companyName}
            onChange={e => setSettings({ ...settings, companyName: e.target.value })} /></label>
          <label>Company Logo Path<input value={settings.companyLogo}
            onChange={e => setSettings({ ...settings, companyLogo: e.target.value })} /></label>
          <label>Default PDF Footer<input value={settings.defaultPdfFooter}
            onChange={e => setSettings({ ...settings, defaultPdfFooter: e.target.value })} /></label>
          <label>Notification Days<input type="number" value={settings.notificationDays}
            onChange={e => setSettings({ ...settings, notificationDays: Number(e.target.value) })} /></label>
          <label>Inspection Draft Warning Days<input type="number"
            value={settings.inspectionDraftWarningDays}
            onChange={e => setSettings({ ...settings,
              inspectionDraftWarningDays: Number(e.target.value) })} /></label>
          <label>Maximum Document Upload Bytes<input type="number"
            value={settings.maximumDocumentUploadBytes}
            onChange={e => setSettings({ ...settings,
              maximumDocumentUploadBytes: Number(e.target.value) })} /></label>
          <label>Maximum Inspection Photo Bytes<input type="number"
            value={settings.maximumInspectionPhotoBytes}
            onChange={e => setSettings({ ...settings,
              maximumInspectionPhotoBytes: Number(e.target.value) })} /></label>
          <label>Maximum Service Request Upload Bytes<input type="number"
            value={settings.maximumServiceRequestUploadBytes}
            onChange={e => setSettings({ ...settings,
              maximumServiceRequestUploadBytes: Number(e.target.value) })} /></label>
          <label>Document Storage Path<input value={settings.documentStoragePath}
            onChange={e => setSettings({ ...settings, documentStoragePath: e.target.value })} /></label>
          <label>Time Zone<input value={settings.timeZone}
            onChange={e => setSettings({ ...settings, timeZone: e.target.value })} /></label>
          <label>Date Format<input value={settings.dateFormat}
            onChange={e => setSettings({ ...settings, dateFormat: e.target.value })} /></label>
          <div><button type="submit" disabled={busy}>Save Settings</button></div>
        </form>}

      {tab === "backups" && <div>
        <div className="button-row">
          {["Database", "Documents", "Full"].map(type =>
            <button key={type} type="button" onClick={() => void createBackup(type)}
              disabled={busy}>{type} Backup</button>)}
        </div>
        <div className="table-scroll"><table><thead><tr>
          <th>Date</th><th>Type</th><th>Size</th><th>Created by</th><th>Action</th>
        </tr></thead><tbody>
          {backups.map(item => <tr key={item.id}>
            <td>{formatDateTime(item.createdUtc)}</td><td>{item.backupType}</td>
            <td>{formatBytes(item.fileSize)}</td><td>{item.createdByName}</td>
            <td><button type="button" className="secondary"
              onClick={() => void restoreBackup(item.id)}>Validate Restore</button></td>
          </tr>)}
          {!backups.length && <tr><td colSpan={5}>No backups available.</td></tr>}
        </tbody></table></div>
      </div>}

      {tab === "health" && health && <AdminObject value={health} />}
      {tab === "diagnostics" && diagnostics && <AdminObject value={diagnostics} />}
      {tab === "audit" && <div className="table-scroll"><table><thead><tr>
        <th>Date</th><th>User</th><th>Action</th><th>Object</th><th>Details</th>
      </tr></thead><tbody>{auditItems.map(item => <tr key={item.id}>
        <td>{formatDateTime(item.createdUtc)}</td><td>{item.userName}</td>
        <td>{item.action}</td><td>{item.objectType} {item.objectId}</td>
        <td>{item.details || "—"}</td>
      </tr>)}
      {!auditItems.length && <tr><td colSpan={5}>No audit entries.</td></tr>}
      </tbody></table></div>}

      {tab === "maintenance" && <div className="administration-actions">
        {[
          ["integrity-check", "Integrity Check"], ["rebuild-indexes", "Rebuild Indexes"],
          ["vacuum", "Vacuum SQLite"], ["cleanup-orphan-files", "Cleanup Orphan Files"],
          ["cleanup-orphan-metadata", "Cleanup Orphan Metadata"],
        ].map(([value, label]) => <button key={value} type="button"
          onClick={() => void maintenance(value)} disabled={busy}>{label}</button>)}
      </div>}

      {tab === "exports" && <div className="export-grid">
        {["audit", "settings", "fleet-summary", "dashboard-statistics"].map(name =>
          <article key={name} className="navigation-card compact">
            <strong>{name.replace(/-/g, " ")}</strong>
            <div className="button-row">
              <button type="button" onClick={() => void downloadExport(name, "csv")}>CSV</button>
              <button type="button" onClick={() => void downloadExport(name, "xlsx")}>Excel</button>
            </div>
          </article>)}
      </div>}
    </section>
  </main>;
}

async function administrationResponseMessage(response: Response) {
  const body = await response.json().catch(() => null);
  return body?.detail ?? body?.title ?? body?.message ?? "Administration request failed.";
}

function AdminObject({ value }: { value: Record<string, any> }) {
  return <div className="admin-object-grid">
    {Object.entries(value).map(([group, details]) =>
      <article className="navigation-card compact" key={group}>
        <h2>{group.replace(/([A-Z])/g, " $1")}</h2>
        {typeof details === "object" && details !== null
          ? Object.entries(details).map(([key, item]) =>
            <div className="admin-property" key={key}>
              <span>{key.replace(/([A-Z])/g, " $1")}</span>
              <strong>{String(item)}</strong>
            </div>)
          : <strong>{String(details)}</strong>}
      </article>)}
  </div>;
}

function formatBytes(value: number) {
  if (value < 1024) return `${value} B`;
  if (value < 1048576) return `${(value / 1024).toFixed(1)} KB`;
  return `${(value / 1048576).toFixed(1)} MB`;
}

function UserAdministrationDialog({
  token,
  vessels,
  onClose,
}: {
  token: string;
  vessels: Vessel[];
  onClose: () => void;
}) {
  const emptyDraft = {
    id: "",
    fullName: "",
    email: "",
    role: "Crew" as LoginResult["role"],
    isActive: true,
    password: "",
    vesselIds: [] as string[],
  };

  const [users, setUsers] = useState<ManagedUser[]>([]);
  const [draft, setDraft] = useState(emptyDraft);
  const [administrationBusy, setAdministrationBusy] =
    useState(false);
  const [administrationError, setAdministrationError] =
    useState("");

  const isNew = !draft.id;

  async function loadUsers() {
    setAdministrationBusy(true);
    setAdministrationError("");

    try {
      const response = await authFetch(
        `${apiBaseUrl}/users`,
        token,
      );

      if (!response.ok)
        throw new Error("Unable to load users.");

      setUsers(await response.json());
    } catch (exception) {
      setAdministrationError(
        (exception as Error).message,
      );
    } finally {
      setAdministrationBusy(false);
    }
  }

  useEffect(() => {
    loadUsers();
  }, []);

  function selectUser(item: ManagedUser) {
    setDraft({
      id: item.id,
      fullName: item.fullName,
      email: item.email,
      role: item.role,
      isActive: item.isActive,
      password: "",
      vesselIds: item.vesselIds,
    });
    setAdministrationError("");
  }

  function newUser() {
    setDraft(emptyDraft);
    setAdministrationError("");
  }

  async function saveUser() {
    if (!draft.fullName.trim()) {
      setAdministrationError(
        "Full Name is required.",
      );
      return;
    }

    if (!draft.email.trim()) {
      setAdministrationError(
        "Email is required.",
      );
      return;
    }

    if (isNew && !draft.password) {
      setAdministrationError(
        "Password is required for a new user.",
      );
      return;
    }

    setAdministrationBusy(true);
    setAdministrationError("");

    try {
      const response = await authFetch(
        isNew
          ? `${apiBaseUrl}/users`
          : `${apiBaseUrl}/users/${draft.id}`,
        token,
        {
          method: isNew ? "POST" : "PUT",
          headers: {
            "Content-Type": "application/json",
          },
          body: JSON.stringify({
            fullName: draft.fullName.trim(),
            email: draft.email.trim(),
            role:
              draft.role === "OfficeAdmin"
                ? 0
                : draft.role === "Superintendent"
                  ? 1
                  : 2,
            isActive: draft.isActive,
            password:
              draft.password || null,
            vesselIds:
              draft.role === "OfficeAdmin"
                ? []
                : draft.vesselIds,
          }),
        },
      );

      if (!response.ok) {
        const body =
          await response.json().catch(() => null);

        throw new Error(
          body?.message ??
          "Unable to save user.",
        );
      }

      const saved: ManagedUser =
        await response.json();

      setUsers(current => {
        const withoutSaved =
          current.filter(x => x.id !== saved.id);

        return [...withoutSaved, saved].sort(
          (a, b) =>
            a.fullName.localeCompare(b.fullName),
        );
      });

      selectUser(saved);
    } catch (exception) {
      setAdministrationError(
        (exception as Error).message,
      );
    } finally {
      setAdministrationBusy(false);
    }
  }

  function toggleVessel(vesselId: string) {
    setDraft(current => {
      const exists =
        current.vesselIds.includes(vesselId);

      return {
        ...current,
        vesselIds: exists
          ? current.vesselIds.filter(
              id => id !== vesselId,
            )
          : [...current.vesselIds, vesselId],
      };
    });
  }

  return (
    <div className="modal">
      <div className="user-administration-dialog">
        <div className="service-request-header">
          <div>
            <h2>Users</h2>
            <p>
              Roles and vessel access.
            </p>
          </div>

          <button
            type="button"
            onClick={onClose}
          >
            Close
          </button>
        </div>

        {administrationError &&
          <div className="error">
            {administrationError}
          </div>}

        <div className="user-administration-layout">
          <aside className="managed-user-list">
            <button
              type="button"
              className="primary"
              onClick={newUser}
            >
              Add User
            </button>

            {administrationBusy &&
              users.length === 0 &&
              <p className="muted">
                Loading...
              </p>}

            {users.map(item => (
              <button
                type="button"
                key={item.id}
                className={
                  draft.id === item.id
                    ? "managed-user selected"
                    : "managed-user"
                }
                onClick={() => selectUser(item)}
              >
                <strong>{item.fullName}</strong>
                <span>{item.email}</span>
                <small>
                  {item.role}
                  {!item.isActive
                    ? " · Inactive"
                    : ""}
                </small>
              </button>
            ))}
          </aside>

          <section className="managed-user-form">
            <h3>
              {isNew ? "Add User" : "Edit User"}
            </h3>

            <label>
              Full Name *
              <input
                value={draft.fullName}
                onChange={event =>
                  setDraft(current => ({
                    ...current,
                    fullName:
                      event.target.value,
                  }))}
              />
            </label>

            <label>
              Email *
              <input
                type="email"
                value={draft.email}
                onChange={event =>
                  setDraft(current => ({
                    ...current,
                    email:
                      event.target.value,
                  }))}
              />
            </label>

            <label>
              Role *
              <select
                value={draft.role}
                onChange={event =>
                  setDraft(current => ({
                    ...current,
                    role:
                      (event.target.value as LoginResult["role"]),
                    vesselIds:
                      event.target.value ===
                        "OfficeAdmin"
                        ? []
                        : current.vesselIds,
                  }))}
              >
                <option>OfficeAdmin</option>
                <option>Superintendent</option>
                <option>Crew</option>
              </select>
            </label>

            <label>
              {isNew
                ? "Password *"
                : "New Password"}
              <input
                type="password"
                value={draft.password}
                onChange={event =>
                  setDraft(current => ({
                    ...current,
                    password:
                      event.target.value,
                  }))}
              />
            </label>

            <label className="active-user-option">
              <input
                type="checkbox"
                checked={draft.isActive}
                onChange={event =>
                  setDraft(current => ({
                    ...current,
                    isActive:
                      event.target.checked,
                  }))}
              />
              Active user
            </label>

            {draft.role !== "OfficeAdmin" &&
              <div className="vessel-assignment-block">
                <strong>Assigned Vessels</strong>

                {vessels.map(vessel => (
                  <label
                    className="vessel-assignment-option"
                    key={vessel.id}
                  >
                    <input
                      type="checkbox"
                      checked={
                        draft.vesselIds.includes(
                          vessel.id,
                        )}
                      onChange={() =>
                        toggleVessel(vessel.id)}
                    />
                    {vessel.vesselName}
                    {" · "}IMO {vessel.imoNumber}
                  </label>
                ))}

                {vessels.length === 0 &&
                  <span className="muted">
                    No vessels available.
                  </span>}
              </div>}

            <div className="service-request-actions">
              <button
                type="button"
                disabled={administrationBusy}
                onClick={newUser}
              >
                Clear
              </button>

              <button
                type="button"
                className="primary"
                disabled={administrationBusy}
                onClick={saveUser}
              >
                {administrationBusy
                  ? "Saving..."
                  : isNew
                    ? "Create User"
                    : "Save Changes"}
              </button>
            </div>
          </section>
        </div>
      </div>
    </div>
  );
}

function VesselProfileDialog({
  token,
  userRole,
  vessel,
  onClose,
  onSaved,
}: {
  token: string;
  userRole: LoginResult["role"];
  vessel: Vessel;
  onClose: () => void;
  onSaved: (vessel: Vessel) => void;
}) {
  const canEdit = userRole === "OfficeAdmin";

  const [editing, setEditing] = useState(false);
  const [profileBusy, setProfileBusy] =
    useState(false);
  const [profileError, setProfileError] =
    useState("");

  const [draft, setDraft] = useState({
    vesselName: vessel.vesselName,
    imoNumber: vessel.imoNumber,
    flag: vessel.flag ?? "",
    companyName: vessel.companyName ?? "",
    invoicingDetails:
      vessel.invoicingDetails ?? "",
    accountingEmail:
      vessel.accountingEmail ?? "",
  });

  function cancelEdit() {
    setDraft({
      vesselName: vessel.vesselName,
      imoNumber: vessel.imoNumber,
      flag: vessel.flag ?? "",
      companyName: vessel.companyName ?? "",
      invoicingDetails:
        vessel.invoicingDetails ?? "",
      accountingEmail:
        vessel.accountingEmail ?? "",
    });
    setEditing(false);
    setProfileError("");
  }

  async function save() {
    if (!draft.vesselName.trim()) {
      setProfileError(
        "Vessel Name is required.",
      );
      return;
    }

    if (!/^\d{7}$/.test(
      draft.imoNumber.trim(),
    )) {
      setProfileError(
        "IMO Number must contain 7 digits.",
      );
      return;
    }

    setProfileBusy(true);
    setProfileError("");

    try {
      const response = await authFetch(
        `${apiBaseUrl}/vessels/${vessel.id}`,
        token,
        {
          method: "PUT",
          headers: {
            "Content-Type": "application/json",
          },
          body: JSON.stringify({
            vesselName:
              draft.vesselName.trim(),
            imoNumber:
              draft.imoNumber.trim(),
            flag:
              draft.flag.trim() || null,
            companyName:
              draft.companyName.trim() || null,
            invoicingDetails:
              draft.invoicingDetails.trim() || null,
            accountingEmail:
              draft.accountingEmail.trim() || null,
          }),
        },
      );

      if (!response.ok) {
        const body =
          await response.json().catch(() => null);

        throw new Error(
          body?.message ??
          "Unable to save Vessel Profile.",
        );
      }

      onSaved(await response.json());
    } catch (exception) {
      setProfileError(
        (exception as Error).message,
      );
    } finally {
      setProfileBusy(false);
    }
  }

  return (
    <div className="modal">
      <div className="vessel-profile-dialog">
        <div className="service-request-header">
          <div>
            <h2>Vessel Profile</h2>
            <p>
              {vessel.vesselName} ·
              {" "}IMO {vessel.imoNumber}
            </p>
          </div>

          <button
            type="button"
            onClick={onClose}
          >
            Close
          </button>
        </div>

        {profileError &&
          <div className="error">
            {profileError}
          </div>}

        <div className="vessel-profile-form">
          <label>
            Vessel Name *
            <input
              disabled={!editing}
              value={draft.vesselName}
              onChange={event =>
                setDraft(current => ({
                  ...current,
                  vesselName:
                    event.target.value,
                }))}
            />
          </label>

          <label>
            IMO Number *
            <input
              disabled={!editing}
              maxLength={7}
              value={draft.imoNumber}
              onChange={event =>
                setDraft(current => ({
                  ...current,
                  imoNumber:
                    event.target.value,
                }))}
            />
          </label>

          <label>
            Flag
            <input
              disabled={!editing}
              value={draft.flag}
              onChange={event =>
                setDraft(current => ({
                  ...current,
                  flag: event.target.value,
                }))}
            />
          </label>

          <label>
            Company Name
            <input
              disabled={!editing}
              value={draft.companyName}
              onChange={event =>
                setDraft(current => ({
                  ...current,
                  companyName:
                    event.target.value,
                }))}
            />
          </label>

          <label className="wide-field">
            Invoicing Details
            <textarea
              disabled={!editing}
              rows={5}
              value={draft.invoicingDetails}
              onChange={event =>
                setDraft(current => ({
                  ...current,
                  invoicingDetails:
                    event.target.value,
                }))}
            />
          </label>

          <label className="wide-field">
            Accounting Email
            <input
              disabled={!editing}
              type="email"
              value={draft.accountingEmail}
              onChange={event =>
                setDraft(current => ({
                  ...current,
                  accountingEmail:
                    event.target.value,
                }))}
            />
          </label>
        </div>

        <div className="service-request-actions">
          {!editing &&
            <>
              <button
                type="button"
                onClick={onClose}
              >
                Close
              </button>

              {canEdit &&
                <button
                  type="button"
                  className="primary"
                  onClick={() =>
                    setEditing(true)}
                >
                  Edit
                </button>}
            </>}

          {editing &&
            <>
              <button
                type="button"
                disabled={profileBusy}
                onClick={cancelEdit}
              >
                Cancel
              </button>

              <button
                type="button"
                className="primary"
                disabled={profileBusy}
                onClick={save}
              >
                {profileBusy
                  ? "Saving..."
                  : "Save"}
              </button>
            </>}
        </div>
      </div>
    </div>
  );
}

function AddEquipmentDialog({
  token,
  vesselId,
  onClose,
  onCreated,
}: {
  token: string;
  vesselId: string;
  onClose: () => void;
  onCreated: (item: Equipment) => void;
}) {
  const [draft, setDraft] = useState({
    category: "LSA" as EquipmentCategory,
    equipmentName: "",
    maker: "",
    model: "",
    serialNumber: "",
    quantity: 1,
    inspectionBy: "Service Company" as InspectionBy,
    expiryDate: "",
    remarks: "",
  });

  const [addBusy, setAddBusy] = useState(false);
  const [addError, setAddError] = useState("");

  async function createEquipment() {
    if (!draft.equipmentName.trim()) {
      setAddError("Equipment Name is required.");
      return;
    }

    if (draft.quantity < 1) {
      setAddError(
        "Quantity must be greater than zero.",
      );
      return;
    }

    setAddBusy(true);
    setAddError("");

    try {
      const response = await authFetch(
        `${apiBaseUrl}/vessels/${vesselId}/equipment`,
        token,
        {
          method: "POST",
          headers: {
            "Content-Type": "application/json",
          },
          body: JSON.stringify({
            category:
              categoryToApi(draft.category),
            equipmentName:
              draft.equipmentName.trim(),
            maker:
              draft.maker.trim() || null,
            model:
              draft.model.trim() || null,
            serialNumber:
              draft.serialNumber.trim() || null,
            quantity:
              draft.quantity,
            inspectionBy:
              inspectionToApi(
                draft.inspectionBy,
              ),
            expiryDate:
              draft.expiryDate || null,
            remarks:
              draft.remarks.trim() || null,
          }),
        },
      );

      if (!response.ok) {
        const body =
          await response.json().catch(() => null);

        throw new Error(
          body?.message ??
          "Unable to add equipment.",
        );
      }

      onCreated(await response.json());
    } catch (exception) {
      setAddError(
        (exception as Error).message,
      );
    } finally {
      setAddBusy(false);
    }
  }

  return (
    <div className="modal">
      <div className="add-equipment-dialog">
        <div className="service-request-header">
          <div>
            <h2>Add Equipment</h2>
            <p>
              Create a new Equipment Register entry.
            </p>
          </div>

          <button
            type="button"
            onClick={onClose}
          >
            Close
          </button>
        </div>

        {addError &&
          <div className="error">
            {addError}
          </div>}

        <div className="add-equipment-form">
          <label>
            Category *
            <select
              value={draft.category}
              onChange={event =>
                setDraft(current => ({
                  ...current,
                  category:
                    (event.target.value as EquipmentCategory),
                }))}
            >
              <option>LSA</option>
              <option>FFE</option>
              <option>Medicine</option>
              <option>Radio & Navigation</option>
            </select>
          </label>

          <label className="wide-field">
            Equipment Name *
            <input
              value={draft.equipmentName}
              onChange={event =>
                setDraft(current => ({
                  ...current,
                  equipmentName:
                    event.target.value,
                }))}
            />
          </label>

          <label>
            Maker
            <input
              value={draft.maker}
              onChange={event =>
                setDraft(current => ({
                  ...current,
                  maker: event.target.value,
                }))}
            />
          </label>

          <label>
            Model
            <input
              value={draft.model}
              onChange={event =>
                setDraft(current => ({
                  ...current,
                  model: event.target.value,
                }))}
            />
          </label>

          <label>
            Serial Number
            <input
              value={draft.serialNumber}
              onChange={event =>
                setDraft(current => ({
                  ...current,
                  serialNumber:
                    event.target.value,
                }))}
            />
          </label>

          <label>
            Quantity *
            <input
              type="number"
              min="1"
              value={draft.quantity}
              onChange={event =>
                setDraft(current => ({
                  ...current,
                  quantity:
                    Number(event.target.value),
                }))}
            />
          </label>

          <label>
            Inspection By *
            <select
              value={draft.inspectionBy}
              onChange={event =>
                setDraft(current => ({
                  ...current,
                  inspectionBy:
                    (event.target.value as InspectionBy),
                }))}
            >
              <option>Crew</option>
              <option>Service Company</option>
            </select>
          </label>

          <label>
            Expiry Date
            <input
              type="date"
              value={draft.expiryDate}
              onChange={event =>
                setDraft(current => ({
                  ...current,
                  expiryDate:
                    event.target.value,
                }))}
            />
          </label>

          <label className="wide-field">
            Remarks
            <textarea
              rows={4}
              value={draft.remarks}
              onChange={event =>
                setDraft(current => ({
                  ...current,
                  remarks:
                    event.target.value,
                }))}
            />
          </label>
        </div>

        <div className="service-request-actions">
          <button
            type="button"
            disabled={addBusy}
            onClick={onClose}
          >
            Cancel
          </button>

          <button
            type="button"
            className="primary"
            disabled={addBusy}
            onClick={createEquipment}
          >
            {addBusy
              ? "Saving..."
              : "Save Equipment"}
          </button>
        </div>
      </div>
    </div>
  );
}

type InspectionListFormat = "excel" | "pdf";

function InspectionListDialog({
  token,
  vessel,
  equipment,
  selectedIds,
  onClose,
  onGenerated,
}: {
  token: string;
  vessel: Vessel;
  equipment: Equipment[];
  selectedIds: Set<string>;
  onClose: () => void;
  onGenerated: () => void;
}) {
  const [format, setFormat] =
    useState<InspectionListFormat>("pdf");
  const [includeCertificates, setIncludeCertificates] =
    useState(false);
  const [inspectionBusy, setInspectionBusy] =
    useState(false);
  const [inspectionError, setInspectionError] =
    useState("");

  const selectedEquipment = equipment.filter(item =>
    selectedIds.has(item.id),
  );

  async function generateInspectionList() {
    if (selectedEquipment.length === 0) {
      setInspectionError(
        "Select at least one equipment item.",
      );
      return;
    }

    setInspectionBusy(true);
    setInspectionError("");

    try {
      const response = await authFetch(
        `${apiBaseUrl}/inspection-list`,
        token,
        {
          method: "POST",
          headers: {
            "Content-Type": "application/json",
          },
          body: JSON.stringify({
            vesselId: vessel.id,
            equipmentIds:
              selectedEquipment.map(item => item.id),
            format,
            includeCertificates,
          }),
        },
      );

      if (!response.ok) {
        const body =
          await response.json().catch(() => null);

        throw new Error(
          body?.message ??
          "Unable to generate Inspection List.",
        );
      }

      const blob = await response.blob();
      const disposition =
        response.headers.get("Content-Disposition");

      const fileNameMatch =
        disposition?.match(
          /filename\*?=(?:UTF-8''|"?)([^";]+)/i,
        );

      const fallbackName = includeCertificates
        ? "Inspection List.zip"
        : format === "pdf"
          ? "Inspection List.pdf"
          : "Inspection List.xlsx";

      const fileName = fileNameMatch
        ? decodeURIComponent(
            fileNameMatch[1].replace(/"/g, ""),
          )
        : fallbackName;

      const url = URL.createObjectURL(blob);
      const anchor = document.createElement("a");
      anchor.href = url;
      anchor.download = fileName;
      document.body.appendChild(anchor);
      anchor.click();
      anchor.remove();
      URL.revokeObjectURL(url);

      onGenerated();
    } catch (exception) {
      setInspectionError(
        (exception as Error).message,
      );
    } finally {
      setInspectionBusy(false);
    }
  }

  return (
    <div className="modal">
      <div className="service-request-dialog">
        <div className="service-request-header">
          <div>
            <h2>Generate Inspection List</h2>
            <p>
              {vessel.vesselName} · IMO {vessel.imoNumber}
            </p>
          </div>

          <button
            type="button"
            onClick={onClose}
          >
            Close
          </button>
        </div>

        {inspectionError &&
          <div className="error">
            {inspectionError}
          </div>}

        <div className="inspection-options">
          <span>Output format</span>

          <label className="attachment-option">
            <input
              type="radio"
              name="inspection-format"
              checked={format === "excel"}
              onChange={() => setFormat("excel")}
            />
            Excel
          </label>

          <label className="attachment-option">
            <input
              type="radio"
              name="inspection-format"
              checked={format === "pdf"}
              onChange={() => setFormat("pdf")}
            />
            Printable PDF
          </label>
        </div>

        <div className="attachments-block">
          <span>Attachments</span>

          <label className="attachment-option">
            <input
              type="checkbox"
              checked={includeCertificates}
              onChange={event =>
                setIncludeCertificates(
                  event.target.checked,
                )}
            />
            Include Previous Certificates
          </label>

          <small>
            Certificates are not included unless
            you select this option. Missing files
            are skipped.
          </small>
        </div>

        <h3>
          Selected Equipment ({selectedEquipment.length})
        </h3>

        <div className="selected-equipment-list">
          {selectedEquipment.map(item => (
            <div
              className="selected-equipment-item"
              key={item.id}
            >
              <strong>{item.equipmentName}</strong>
              <span>
                {item.category}
                {item.serialNumber
                  ? ` · ${item.serialNumber}`
                  : ""}
                {item.expiryDate
                  ? ` · Expiry ${formatDate(item.expiryDate)}`
                  : ""}
              </span>
            </div>
          ))}
        </div>

        <div className="service-request-actions">
          <button
            type="button"
            disabled={inspectionBusy}
            onClick={onClose}
          >
            Cancel
          </button>

          <button
            type="button"
            className="primary"
            disabled={inspectionBusy}
            onClick={generateInspectionList}
          >
            {inspectionBusy
              ? "Generating..."
              : includeCertificates
                ? "Generate ZIP"
                : format === "pdf"
                  ? "Generate Printable PDF"
                  : "Generate Excel"}
          </button>
        </div>
      </div>
    </div>
  );
}

function ServiceRequestDialog({
  token,
  vessel,
  equipment,
  selectedIds,
  onClose,
  onGenerated,
}: {
  token: string;
  vessel: Vessel;
  equipment: Equipment[];
  selectedIds: Set<string>;
  onClose: () => void;
  onGenerated: () => void;
}) {
  const [port, setPort] = useState("");
  const [eta, setEta] = useState("");
  const [etd, setEtd] = useState("");
  const [remarks, setRemarks] = useState("");
  const [includeCertificates, setIncludeCertificates] =
    useState(false);
  const [includeReports, setIncludeReports] =
    useState(false);
  const [requestBusy, setRequestBusy] = useState(false);
  const [requestError, setRequestError] = useState("");

  const selectedEquipment = equipment.filter(item =>
    selectedIds.has(item.id),
  );

  async function generate() {

    if (selectedEquipment.length === 0) {
      setRequestError(
        "Select at least one equipment item.",
      );
      return;
    }

    setRequestBusy(true);
    setRequestError("");

    try {
      const response = await authFetch(
        `${apiBaseUrl}/service-request`,
        token,
        {
          method: "POST",
          headers: {
            "Content-Type": "application/json",
          },
          body: JSON.stringify({
            vesselId: vessel.id,
            equipmentIds:
              selectedEquipment.map(item => item.id),
            port: port.trim() || null,
            eta: eta || null,
            etd: etd || null,
            remarks: remarks.trim() || null,
            includeCertificates,
            includeReports,
          }),
        },
      );

      if (!response.ok) {
        const body =
          await response.json().catch(() => null);

        throw new Error(
          body?.message ??
          "Unable to generate Service Request.",
        );
      }

      const blob = await response.blob();
      const disposition =
        response.headers.get("Content-Disposition");

      const fileNameMatch =
        disposition?.match(
          /filename\*?=(?:UTF-8''|"?)([^";]+)/i,
        );

      const contentType =
        response.headers.get("Content-Type") ?? "";

      const fileName = fileNameMatch
        ? decodeURIComponent(
            fileNameMatch[1].replace(/"/g, ""),
          )
        : contentType.includes("application/zip")
          ? "Service Request.zip"
          : "Service Request.xlsx";

      const url = URL.createObjectURL(blob);
      const anchor = document.createElement("a");
      anchor.href = url;
      anchor.download = fileName;
      document.body.appendChild(anchor);
      anchor.click();
      anchor.remove();
      URL.revokeObjectURL(url);

      onGenerated();
    } catch (exception) {
      setRequestError(
        (exception as Error).message,
      );
    } finally {
      setRequestBusy(false);
    }
  }

  return (
    <div className="modal">
      <div className="service-request-dialog">
        <div className="service-request-header">
          <div>
            <h2>Generate Service Request</h2>
            <p>
              {vessel.vesselName} · IMO {vessel.imoNumber}
            </p>
          </div>
          <button
            type="button"
            onClick={onClose}
          >
            Close
          </button>
        </div>

        {requestError &&
          <div className="error">
            {requestError}
          </div>}

        <div className="service-request-form">
          <label>
            Port
            <input
              value={port}
              onChange={event =>
                setPort(event.target.value)}
            />
          </label>

          <label>
            ETA
            <input
              type="date"
              value={eta}
              onChange={event =>
                setEta(event.target.value)}
            />
          </label>

          <label>
            ETD
            <input
              type="date"
              value={etd}
              onChange={event =>
                setEtd(event.target.value)}
            />
          </label>

          <label className="wide-field">
            Remarks
            <textarea
              rows={5}
              value={remarks}
              onChange={event =>
                setRemarks(event.target.value)}
            />
          </label>

          <div className="attachments-block wide-field">
            <span>Attachments</span>

            <label className="attachment-option">
              <input
                type="checkbox"
                checked={includeCertificates}
                onChange={event =>
                  setIncludeCertificates(
                    event.target.checked,
                  )}
              />
              Include Previous Certificates
            </label>

            <label className="attachment-option">
              <input
                type="checkbox"
                checked={includeReports}
                onChange={event =>
                  setIncludeReports(
                    event.target.checked,
                  )}
              />
              Include Previous Reports
            </label>

            <small>
              Nothing is attached unless you select it.
              Missing documents will be skipped.
            </small>
          </div>

        </div>

        <h3>
          Selected Equipment ({selectedEquipment.length})
        </h3>

        <div className="selected-equipment-list">
          {selectedEquipment.map(item => (
            <div
              className="selected-equipment-item"
              key={item.id}
            >
              <strong>{item.equipmentName}</strong>
              <span>
                {item.category}
                {item.maker ? ` · ${item.maker}` : ""}
                {item.model ? ` · ${item.model}` : ""}
                {item.quantity > 1
                  ? ` · Qty ${item.quantity}`
                  : ""}
              </span>
            </div>
          ))}
        </div>

        <div className="service-request-actions">
          <button
            type="button"
            disabled={requestBusy}
            onClick={onClose}
          >
            Cancel
          </button>

          <button
            type="button"
            className="primary"
            disabled={requestBusy}
            onClick={generate}
          >
            {requestBusy
              ? "Generating..."
              : includeCertificates || includeReports
                ? "Generate ZIP"
                : "Generate Excel"}
          </button>
        </div>
      </div>
    </div>
  );
}

function EquipmentDetails({
  item,
  token,
  vesselId,
  onSaved,
  onDeleted,
  onDocumentsChanged,
}: {
  item: Equipment;
  token: string;
  vesselId: string;
  onSaved: (item: Equipment) => void;
  onDeleted: (id: string) => void;
  onDocumentsChanged: () => Promise<void>;
}) {
  const [draft, setDraft] = useState(item);
  const [editing, setEditing] = useState(false);
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState("");

  const update = <K extends keyof Equipment>(key: K, value: Equipment[K]) =>
    setDraft(current => ({ ...current, [key]: value }));

  if (!editing) {
    return (
      <>
        <div className="details-header">
          <h2>{item.equipmentName}</h2>
          <button className="primary" onClick={() => setEditing(true)}>Edit</button>
        </div>
        <div className="details-grid">
          <Detail label="Category" value={item.category} />
          <Detail label="Maker" value={item.maker || "—"} />
          <Detail label="Model" value={item.model || "—"} />
          <Detail label="Serial Number" value={item.serialNumber || "—"} />
          <Detail label="Quantity" value={String(item.quantity)} />
          <Detail label="Inspection By" value={item.inspectionBy} />
          <Detail label="Expiry Date" value={formatDate(item.expiryDate)} />
          <Detail label="Status" value={item.status} />
        </div>
        <div className="remarks">
          <span>Remarks</span>
          <p>{item.remarks || "—"}</p>
        </div>

        <section className="documents-section">
          <h3>Reports & Certificates</h3>
          <DocumentControl
            token={token}
            equipmentId={item.id}
            type="report"
            available={item.reportAvailable}
            fileName={item.reportFileName}
            onChanged={onDocumentsChanged}
          />
          <DocumentControl
            token={token}
            equipmentId={item.id}
            type="certificate"
            available={item.certificateAvailable}
            fileName={item.certificateFileName}
            onChanged={onDocumentsChanged}
          />
        </section>

      </>
    );
  }

  return (
    <form
      onSubmit={async event => {
        event.preventDefault();
        if (!draft.equipmentName.trim()) {
          setError("Equipment Name is required.");
          return;
        }
        if (draft.quantity < 1) {
          setError("Quantity must be greater than zero.");
          return;
        }

        setBusy(true);
        setError("");
        try {
          const response = await authFetch(
            `${apiBaseUrl}/vessels/${vesselId}/equipment/${draft.id}`,
            token,
            {
              method: "PUT",
              headers: { "Content-Type": "application/json" },
              body: JSON.stringify({
                category: categoryToApi(draft.category),
                equipmentName: draft.equipmentName,
                maker: draft.maker || null,
                model: draft.model || null,
                serialNumber: draft.serialNumber || null,
                quantity: draft.quantity,
                inspectionBy: inspectionToApi(draft.inspectionBy),
                expiryDate: draft.expiryDate || null,
                remarks: draft.remarks || null,
              }),
            },
          );

          if (!response.ok) {
            const body = await response.json().catch(() => null);
            throw new Error(body?.message ?? "Unable to save equipment.");
          }

          const saved: Equipment = await response.json();
          onSaved(saved);
          setDraft(saved);
          setEditing(false);
        } catch (exception) {
          setError((exception as Error).message);
        } finally {
          setBusy(false);
        }
      }}
    >
      <div className="details-header">
        <h2>Edit Equipment</h2>
      </div>

      {error && <div className="error">{error}</div>}

      <div className="edit-grid">
        <label>
          Category *
          <select
            value={draft.category}
            onChange={event => update("category", event.target.value as Equipment["category"])}
          >
            {categories.map(category => <option key={category}>{category}</option>)}
          </select>
        </label>

        <label>
          Equipment *
          <input
            value={draft.equipmentName}
            onChange={event => update("equipmentName", event.target.value)}
          />
        </label>

        <label>
          Maker
          <input
            value={draft.maker || ""}
            onChange={event => update("maker", event.target.value)}
          />
        </label>

        <label>
          Model
          <input
            value={draft.model || ""}
            onChange={event => update("model", event.target.value)}
          />
        </label>

        <label>
          Serial Number
          <input
            value={draft.serialNumber || ""}
            onChange={event => update("serialNumber", event.target.value)}
          />
        </label>

        <label>
          Quantity *
          <input
            type="number"
            min={1}
            value={draft.quantity}
            onChange={event => update("quantity", Number(event.target.value))}
          />
        </label>

        <label>
          Inspection By *
          <select
            value={draft.inspectionBy}
            onChange={event => update("inspectionBy", event.target.value as Equipment["inspectionBy"])}
          >
            <option>Crew</option>
            <option>Service Company</option>
          </select>
        </label>

        <label>
          Expiry Date
          <input
            type="date"
            value={draft.expiryDate || ""}
            onChange={event => update("expiryDate", event.target.value)}
          />
        </label>

        <label className="wide-field">
          Remarks
          <textarea
            rows={5}
            value={draft.remarks || ""}
            onChange={event => update("remarks", event.target.value)}
          />
        </label>
      </div>

      <div className="details-actions">
        <button
          type="button"
          className="danger"
          disabled={busy}
          onClick={async () => {
            if (!window.confirm("Delete selected equipment?")) return;
            setBusy(true);
            setError("");
            try {
              const response = await authFetch(
                `${apiBaseUrl}/vessels/${vesselId}/equipment/${draft.id}`,
                token,
                { method: "DELETE" },
              );
              if (!response.ok) throw new Error("Unable to delete equipment.");
              onDeleted(draft.id);
            } catch (exception) {
              setError((exception as Error).message);
            } finally {
              setBusy(false);
            }
          }}
        >
          Delete
        </button>
        <button
          type="button"
          disabled={busy}
          onClick={() => {
            setDraft(item);
            setEditing(false);
            setError("");
          }}
        >
          Cancel
        </button>
        <button className="primary" type="submit" disabled={busy}>
          {busy ? "Saving..." : "Save"}
        </button>
      </div>
    </form>
  );
}


type DocumentType = "report" | "certificate";

function DocumentControl({
  token,
  equipmentId,
  type,
  available,
  fileName,
  onChanged,
}: {
  token: string;
  equipmentId: string;
  type: DocumentType;
  available: boolean;
  fileName?: string;
  onChanged: () => Promise<void>;
}) {
  const inputId = `${equipmentId}-${type}`;
  const [documentBusy, setDocumentBusy] = useState(false);
  const [documentError, setDocumentError] = useState("");

  const label =
    type === "report"
      ? "Service Report"
      : "Service Certificate";

  async function upload(file?: File) {
    if (!file) return;

    setDocumentBusy(true);
    setDocumentError("");

    try {
      const form = new FormData();
      form.append("file", file);

      const response = await authFetch(
        `${apiBaseUrl}/equipment/${equipmentId}/documents/${type}`,
        token,
        {
          method: "POST",
          body: form,
        },
      );

      if (!response.ok) {
        const body = await response.json().catch(() => null);
        throw new Error(
          body?.message ?? `Unable to upload ${label}.`,
        );
      }

      await onChanged();
    } catch (exception) {
      setDocumentError((exception as Error).message);
    } finally {
      setDocumentBusy(false);
      const input = document.getElementById(
        inputId,
      ) as HTMLInputElement | null;

      if (input) input.value = "";
    }
  }

  async function openDocument() {
    setDocumentBusy(true);
    setDocumentError("");

    try {
      const response = await authFetch(
        `${apiBaseUrl}/equipment/${equipmentId}/documents/${type}`,
        token,
      );

      if (!response.ok)
        throw new Error(`Unable to open ${label}.`);

      const blob = await response.blob();
      const url = URL.createObjectURL(blob);
      window.open(url, "_blank", "noopener");

      window.setTimeout(
        () => URL.revokeObjectURL(url),
        60_000,
      );
    } catch (exception) {
      setDocumentError((exception as Error).message);
    } finally {
      setDocumentBusy(false);
    }
  }

  async function deleteDocument() {
    if (!window.confirm(`Delete ${label}?`))
      return;

    setDocumentBusy(true);
    setDocumentError("");

    try {
      const response = await authFetch(
        `${apiBaseUrl}/equipment/${equipmentId}/documents/${type}`,
        token,
        { method: "DELETE" },
      );

      if (!response.ok)
        throw new Error(`Unable to delete ${label}.`);

      await onChanged();
    } catch (exception) {
      setDocumentError((exception as Error).message);
    } finally {
      setDocumentBusy(false);
    }
  }

  return (
    <div className="document-control">
      <div className="document-info">
        <strong>{label}</strong>
        <span>
          {available
            ? fileName || "Current PDF available"
            : "No document uploaded"}
        </span>
        {documentError &&
          <small className="document-error">
            {documentError}
          </small>}
      </div>

      <div className="document-actions">
        {available &&
          <button
            type="button"
            disabled={documentBusy}
            onClick={openDocument}
          >
            Open
          </button>}

        <label
          className={`button-label ${
            documentBusy ? "disabled" : ""
          }`}
          htmlFor={inputId}
        >
          {available ? "Replace" : "Upload"}
        </label>

        <input
          id={inputId}
          className="hidden-file-input"
          type="file"
          accept=".pdf,application/pdf"
          disabled={documentBusy}
          onChange={event =>
            upload(event.target.files?.[0])}
        />

        {available &&
          <button
            type="button"
            className="danger"
            disabled={documentBusy}
            onClick={deleteDocument}
          >
            Delete
          </button>}
      </div>
    </div>
  );
}

function FleetOperationsDashboard({
  data, role, vesselFilter, superintendentFilter, statusFilter,
  categoryFilter, yearFilter, search, onVesselFilter,
  onSuperintendentFilter, onStatusFilter, onCategoryFilter,
  onYearFilter, onSearch, onOpenVessel,
}: {
  data: FleetDashboardData; role: LoginResult["role"];
  vesselFilter: string; superintendentFilter: string; statusFilter: string;
  categoryFilter: string; yearFilter: string; search: string;
  onVesselFilter: (value: string) => void;
  onSuperintendentFilter: (value: string) => void;
  onStatusFilter: (value: string) => void;
  onCategoryFilter: (value: string) => void;
  onYearFilter: (value: string) => void;
  onSearch: (value: string) => void;
  onOpenVessel: (id: string) => void;
}) {
  const countGroups = [
    ["Equipment", [
      ["Total", data.counts.equipmentTotal], ["OK", data.counts.equipmentOk],
      ["Soon", data.counts.equipmentSoon], ["Overdue", data.counts.equipmentOverdue],
    ]],
    ["Inspection Reports", [
      ["Draft", data.counts.inspectionDraft],
      ["Finalized", data.counts.inspectionFinalized],
    ]],
    ["Open Findings", [
      ["Critical", data.counts.criticalOpen], ["Urgent", data.counts.urgentOpen],
      ["Normal", data.counts.normalOpen],
    ]],
    ["Service Requests", [
      ["Draft", data.counts.serviceDraft], ["Ready", data.counts.serviceReady],
      ["Sent", data.counts.serviceSent], ["Completed", data.counts.serviceCompleted],
      ["Cancelled", data.counts.serviceCancelled],
    ]],
  ] as Array<[string, Array<[string, number]>]>;
  const currentYear = new Date().getFullYear();
  return <div className="fleet-dashboard">
    <section className="fleet-dashboard-filters" aria-label="Fleet dashboard filters">
      <label>Search<input value={search} onChange={e => onSearch(e.target.value)}
        placeholder="Vessel or IMO" /></label>
      <label>Vessel<select value={vesselFilter} onChange={e => onVesselFilter(e.target.value)}>
        <option value="">All</option>{data.vesselOptions.map(x =>
          <option key={x.id} value={x.id}>{x.name}</option>)}</select></label>
      {role === "OfficeAdmin" && <label>Superintendent<select value={superintendentFilter}
        onChange={e => onSuperintendentFilter(e.target.value)}>
        <option value="">All</option>{data.superintendentOptions.map(x =>
          <option key={x.id} value={x.id}>{x.name}</option>)}</select></label>}
      <label>Status<select value={statusFilter} onChange={e => onStatusFilter(e.target.value)}>
        <option value="">All</option><option>Green</option><option>Yellow</option><option>Red</option>
      </select></label>
      <label>Category<select value={categoryFilter} onChange={e => onCategoryFilter(e.target.value)}>
        <option value="">All</option>{categories.map(x => <option key={x}>{x}</option>)}
      </select></label>
      <label>Year<select value={yearFilter} onChange={e => onYearFilter(e.target.value)}>
        <option value="">All</option>{Array.from({length: 6}, (_, i) => currentYear - i)
          .map(x => <option key={x}>{x}</option>)}</select></label>
    </section>
    <div className="fleet-count-groups">{countGroups.map(([title, values]) =>
      <section className="fleet-count-group" key={title}><h2>{title}</h2>
        <div>{values.map(([label, value]) => <div className={`fleet-count-card ${label.toLowerCase()}`}
          key={label}><span>{label}</span><strong>{value}</strong></div>)}</div>
      </section>)}</div>
    <section className="dashboard-section">
      <div className="dashboard-section-heading"><h2>Fleet Operational Status</h2>
        <span className="muted">Select a vessel to focus its dashboard.</span></div>
      {data.vessels.length === 0 ? <div className="empty-state">No vessels match these filters.</div> :
        <div className="table-scroll"><table className="dashboard-table fleet-table">
          <thead><tr><th>Vessel</th><th>IMO</th><th>Equipment overdue</th>
            <th>Critical findings</th><th>Open findings</th><th>Draft inspections</th>
            <th>Active service requests</th><th>Last synchronization</th><th>Overall</th></tr></thead>
          <tbody>{data.vessels.map(x => <tr key={x.vesselId}
            onClick={() => onOpenVessel(x.vesselId)}>
            <td><strong>{x.vesselName}</strong></td><td>{x.imoNumber}</td>
            <td>{x.equipmentOverdue}</td><td>{x.criticalFindings}</td>
            <td>{x.openFindings}</td><td>{x.draftInspectionReports}</td>
            <td>{x.activeServiceRequests}</td>
            <td>{x.lastSynchronizationUtc ? formatDateTime(x.lastSynchronizationUtc) : "Not available"}</td>
            <td><span className={`fleet-status ${x.overallStatus.toLowerCase()}`}>{x.overallStatus}</span></td>
          </tr>)}</tbody></table></div>}
    </section>
    <div className="fleet-dashboard-lower">
      <section className="dashboard-section"><h2>Operational Charts</h2>
        <div className="fleet-charts">{data.charts.map(chart => {
          const maximum = Math.max(1, ...chart.values.map(x => x.value));
          return <article key={chart.name}><h3>{chart.name}</h3>
            {chart.values.map(value => <div className="fleet-chart-row" key={value.label}>
              <span>{value.label}</span><div><i style={{width: `${value.value / maximum * 100}%`}} /></div>
              <strong>{value.value}</strong></div>)}</article>;
        })}</div></section>
      <section className="dashboard-section notification-center">
        <div className="dashboard-section-heading"><h2>Notifications</h2>
          <span className="muted">Newest first</span></div>
        {data.notifications.length === 0 ? <div className="empty-state">No operational notifications.</div> :
          <div>{data.notifications.map((item, index) =>
            <article key={`${item.source}-${item.vesselId}-${item.eventUtc}-${index}`}
              className={`notification-item ${item.severity.toLowerCase()}`}>
              <span>{item.source}</span><div><strong>{item.title}</strong>
                <p>{item.vesselName} · {item.detail}</p></div>
              <time>{formatDateTime(item.eventUtc)}</time>
            </article>)}</div>}
      </section>
    </div>
  </div>;
}

function ManagedServiceRequestsPage({ user, vessels }: {
  user: LoginResult; vessels: Vessel[];
}) {
  const [items, setItems] = useState<ManagedRequestSummary[]>([]);
  const [request, setRequest] = useState<ManagedServiceRequest | null>(null);
  const [pdfs, setPdfs] = useState<InspectionPdf[]>([]);
  const [error, setError] = useState("");
  const [busy, setBusy] = useState(false);
  const [createOpen, setCreateOpen] = useState(false);
  const [selection, setSelection] = useState<"Equipment" | "InspectionFinding" | null>(null);
  const [statusFilter, setStatusFilter] = useState("All");
  const [vesselFilter, setVesselFilter] = useState("All");
  const [yearFilter, setYearFilter] = useState("All");
  const [search, setSearch] = useState("");
  async function api(path: string, init: RequestInit = {}) {
    const response = await authFetch(`${apiBaseUrl}${path}`, user.token, init);
    if (!response.ok) {
      const body = await response.json().catch(() => null);
      throw new Error(body?.detail ?? body?.title ?? "Request failed.");
    }
    return response;
  }
  async function list() {
    setBusy(true); setError("");
    try { setItems(await (await api("/service-requests")).json()); }
    catch (reason) { setError((reason as Error).message); }
    finally { setBusy(false); }
  }
  async function open(id: string) {
    setBusy(true); setError("");
    try {
      const [detail, versions] = await Promise.all([
        api(`/service-requests/${id}`).then(x => x.json()),
        api(`/service-requests/${id}/pdf-versions`).then(x => x.json()),
      ]);
      setRequest(detail); setPdfs(versions);
    } catch (reason) { setError((reason as Error).message); }
    finally { setBusy(false); }
  }
  async function post(path: string, body?: unknown) {
    setBusy(true); setError("");
    try {
      await api(path, { method: "POST",
        headers: body ? { "Content-Type": "application/json" } : undefined,
        body: body ? JSON.stringify(body) : undefined });
      if (request) await open(request.id); else await list();
    } catch (reason) { setError((reason as Error).message); }
    finally { setBusy(false); }
  }
  async function download(path: string, name: string) {
    try {
      const response = await api(path); const url = URL.createObjectURL(await response.blob());
      const anchor = document.createElement("a"); anchor.href = url; anchor.download = name;
      document.body.appendChild(anchor); anchor.click(); anchor.remove(); URL.revokeObjectURL(url);
    } catch (reason) { setError((reason as Error).message); }
  }
  useEffect(() => { void list(); }, []);
  const years = [...new Set(items.map(x => x.requestDate.slice(0, 4)))].sort().reverse();
  const visible = items.filter(x =>
    (statusFilter === "All" || x.status === statusFilter) &&
    (vesselFilter === "All" || x.vesselId === vesselFilter) &&
    (yearFilter === "All" || x.requestDate.startsWith(yearFilter)) &&
    (!search.trim() || `${x.requestNumber} ${x.subject}`.toLowerCase()
      .includes(search.trim().toLowerCase())));
  if (!request) return <main className="workspace managed-requests">
    <div className="workspace-heading"><div><h1>Service Requests</h1>
      <p className="muted">Vessel equipment servicing requests and PDF versions.</p></div>
      <button className="primary" onClick={() => setCreateOpen(true)}>Create Request</button></div>
    {error && <div className="error">{error}</div>}
    <section className="equipment-toolbar">
      <label>Search<input value={search} onChange={e => setSearch(e.target.value)}
        placeholder="Request number or subject" /></label>
      <label>Vessel<select value={vesselFilter} onChange={e => setVesselFilter(e.target.value)}>
        <option>All</option>{vessels.map(x => <option key={x.id} value={x.id}>{x.vesselName}</option>)}</select></label>
      <label>Status<select value={statusFilter} onChange={e => setStatusFilter(e.target.value)}>
        <option>All</option>{["Draft","Ready","Sent","Completed","Cancelled"].map(x => <option key={x}>{x}</option>)}</select></label>
      <label>Year<select value={yearFilter} onChange={e => setYearFilter(e.target.value)}>
        <option>All</option>{years.map(x => <option key={x}>{x}</option>)}</select></label>
    </section>
    {busy ? <div className="loading">Loading service requests...</div> :
      visible.length === 0 ? <div className="empty-state">No service requests available.</div> :
      <div className="table-scroll"><table className="equipment-table">
        <thead><tr><th>Request number</th><th>Vessel</th><th>Date</th><th>Subject</th>
          <th>Port</th><th>Status</th><th>Items</th><th>Created by</th><th>Updated</th><th>Actions</th></tr></thead>
        <tbody>{visible.map(x => <tr key={x.id}><td><strong>{x.requestNumber}</strong></td>
          <td>{x.vesselName}</td><td>{formatDate(x.requestDate)}</td><td>{x.subject}</td>
          <td>{x.preferredPort ?? "—"}</td><td><span className="status">{x.status}</span></td>
          <td>{x.itemCount}</td><td>{x.requestedBy}</td><td>{formatDateTime(x.updatedUtc)}</td>
          <td className="row-actions"><button onClick={() => void open(x.id)}>Open</button>
            <button onClick={async () => { await post(`/service-requests/${x.id}/generate-pdf`); await open(x.id); }}>Generate PDF</button></td>
        </tr>)}</tbody></table></div>}
    {createOpen && <ManagedRequestCreate user={user} vessels={vessels}
      onClose={() => setCreateOpen(false)}
      onCreated={id => { setCreateOpen(false); void open(id); }} />}
  </main>;
  const editable = request.status === "Draft" || request.status === "Ready";
  const crewEditable = user.role !== "Crew" || request.status === "Draft";
  return <main className="workspace managed-requests">
    <div className="workspace-heading"><div>
      <button className="back-button" onClick={() => { setRequest(null); void list(); }}>← Service Requests</button>
      <h1>{request.requestNumber}</h1><p className="muted">{request.vesselName} · IMO {request.imoNumber} · {request.status}</p></div>
      <div className="workspace-actions">
        {editable && crewEditable && <button onClick={async () => {
          const subject = window.prompt("Subject:", request.subject);
          if (!subject) return;
          const contactName = window.prompt("Contact name:", request.contactName);
          if (!contactName) return;
          const contactEmail = window.prompt("Contact email:", request.contactEmail);
          if (!contactEmail) return;
          const preferredPort = window.prompt(
            "Preferred port (leave blank if not required):",
            request.preferredPort ?? "");
          if (preferredPort === null) return;
          await api(`/service-requests/${request.id}`, {
            method: "PUT", headers: { "Content-Type": "application/json" },
            body: JSON.stringify({
              requestDate: request.requestDate, subject, contactName, contactEmail,
              contactPhone: request.contactPhone, preferredPort: preferredPort || null,
              expectedArrivalDate: request.expectedArrivalDate,
              expectedDepartureDate: request.expectedDepartureDate,
              serviceCompany: request.serviceCompany, attentionTo: request.attentionTo,
              generalRemarks: request.generalRemarks,
              invoicingDetails: request.invoicingDetails,
            }),
          });
          await open(request.id);
        }}>Edit Request</button>}
        {editable && <button onClick={() =>
          void post(`/service-requests/${request.id}/refresh-from-vessel`)}>
          Refresh from Vessel
        </button>}
        {editable && crewEditable && <button onClick={() => setSelection("Equipment")}>Add Equipment</button>}
        {editable && user.role !== "Crew" && <button onClick={() => setSelection("InspectionFinding")}>Add Finding</button>}
        {editable && crewEditable && <button onClick={async () => {
          const title = window.prompt("Manual item title:"); if (!title) return;
          const serviceDescription = window.prompt("Required service:"); if (!serviceDescription) return;
          await post(`/service-requests/${request.id}/items`, {
            itemType: "Manual", title, location: null, description: null,
            serviceDescription, remarks: null,
          });
        }}>Add Manual Item</button>}
        <button onClick={() => void post(`/service-requests/${request.id}/generate-pdf`)}>Generate PDF</button>
        {request.status === "Draft" && user.role !== "Crew" &&
          <button className="primary" onClick={() => void post(`/service-requests/${request.id}/mark-ready`)}>Mark Ready</button>}
        {request.status === "Ready" && user.role !== "Crew" &&
          <button className="primary" onClick={() => void post(`/service-requests/${request.id}/mark-sent`)}>Mark Sent</button>}
        {request.status === "Sent" && user.role !== "Crew" &&
          <button className="primary" onClick={() => void post(`/service-requests/${request.id}/complete`)}>Complete</button>}
        {editable && user.role !== "Crew" && <button className="danger" onClick={() => {
          const reason = window.prompt("Cancellation reason:");
          if (reason) void post(`/service-requests/${request.id}/cancel`, { reason });
        }}>Cancel</button>}
        {(request.status === "Sent" || request.status === "Cancelled") && user.role === "OfficeAdmin" &&
          <button onClick={() => { const reason = window.prompt("Reopening reason:");
            if (reason) void post(`/service-requests/${request.id}/reopen`, { reason }); }}>Reopen</button>}
      </div></div>
    {error && <div className="error">{error}</div>}
    <section className="details-card request-header-grid">
      <Detail label="Request date" value={formatDate(request.requestDate)} />
      <Detail label="Subject" value={request.subject} />
      <Detail label="Contact" value={`${request.contactName} · ${request.contactEmail}`} />
      <Detail label="Preferred port" value={request.preferredPort ?? "—"} />
      <Detail label="ETA / ETD" value={`${formatDate(request.expectedArrivalDate)} / ${formatDate(request.expectedDepartureDate)}`} />
      <Detail label="Service company" value={request.serviceCompany ?? "—"} />
      <Detail label="Invoicing details" value={request.invoicingDetails ?? "—"} />
      <Detail label="General remarks" value={request.generalRemarks ?? "—"} />
    </section>
    <section className="dashboard-section"><div className="dashboard-section-heading"><h2>Request Items</h2>
      {editable && <button onClick={() => void post(`/service-requests/${request.id}/items/refresh-equipment`)}>Refresh Equipment Snapshots</button>}</div>
      {request.items.length === 0 ? <div className="empty-state">No request items.</div> :
        <div className="table-scroll"><table className="equipment-table"><thead><tr>
          <th>No.</th><th>Type</th><th>Equipment / area</th><th>Maker / model</th>
          <th>Serial</th><th>Location</th><th>Due / priority</th><th>Required service</th><th>Actions</th>
        </tr></thead><tbody>{request.items.map((x, index) => <tr key={x.id}>
          <td>{x.sequenceNumber}</td><td>{x.itemType}</td><td><strong>{x.title}</strong>
            {x.inspectionReportNumber && <small>{x.inspectionReportNumber} · Finding {x.inspectionFindingSequence}</small>}</td>
          <td>{`${x.maker ?? ""} ${x.model ?? ""}`.trim() || "—"}</td><td>{x.serialNumber ?? "—"}</td>
          <td>{x.location ?? "—"}</td><td>{x.priority ?? formatDate(x.currentDueDate)}</td>
          <td>{x.serviceDescription}</td><td className="row-actions">
            {editable && <><button disabled={index === 0} onClick={() => void reorderServiceItems(request, x.id, -1, api, open)}>↑</button>
              <button disabled={index === request.items.length - 1} onClick={() => void reorderServiceItems(request, x.id, 1, api, open)}>↓</button>
              <button className="danger" onClick={async () => { await api(`/service-request-items/${x.id}`, { method: "DELETE" }); await open(request.id); }}>Remove</button></>}
          </td></tr>)}</tbody></table></div>}
    </section>
    <section className="dashboard-section"><div className="dashboard-section-heading"><h2>Attachments</h2>
      {editable && <label className="button-like">Attach File<input type="file" className="hidden-file-input"
        accept=".pdf,.jpg,.jpeg,.png,.docx,.xlsx" onChange={async e => {
          const file = e.target.files?.[0]; if (!file) return;
          const data = new FormData(); data.append("file", file);
          await api(`/service-requests/${request.id}/attachments`, { method: "POST", body: data });
          await open(request.id);
        }} /></label>}</div>
      {request.attachments.length === 0 ? <div className="empty-state">No attachments.</div> :
        <div className="document-list">{request.attachments.map(x => <div className="document-row" key={x.id}>
          <div><strong>{x.originalFileName}</strong><small>{x.createdBy} · {formatDateTime(x.createdUtc)}</small></div>
          <div><button onClick={() => void download(`/service-request-attachments/${x.id}/download`, x.originalFileName)}>Open</button>
            {editable && <button className="danger" onClick={async () => { await api(`/service-request-attachments/${x.id}`, { method: "DELETE" }); await open(request.id); }}>Remove</button>}</div>
        </div>)}</div>}
    </section>
    <section className="dashboard-section"><h2>PDF Versions</h2>
      {pdfs.length === 0 ? <div className="empty-state">No PDF generated.</div> :
        <div className="document-list">{pdfs.map(x => <div className="document-row" key={x.id}>
          <div><strong>{x.fileName}</strong><small>Version {x.versionNumber} · {formatDateTime(x.generatedUtc)}</small></div>
          <div>{x.isCurrent ? <span className="document-badge current">Current</span> :
            <span className="document-badge archived">Archive</span>}
            <button onClick={() => void download(`/service-request-pdfs/${x.id}/download`, x.fileName)}>Open</button></div>
        </div>)}</div>}
    </section>
    {selection && <ManagedItemSelection request={request} type={selection} token={user.token}
      onClose={() => setSelection(null)}
      onAdded={() => { setSelection(null); void open(request.id); }} />}
  </main>;
}

async function reorderServiceItems(request: ManagedServiceRequest, id: string, offset: number,
  api: (path: string, init?: RequestInit) => Promise<Response>,
  refresh: (id: string) => Promise<void>) {
  const ids = request.items.map(x => x.id); const index = ids.indexOf(id);
  [ids[index], ids[index + offset]] = [ids[index + offset], ids[index]];
  await api(`/service-requests/${request.id}/items/reorder`, {
    method: "POST", headers: { "Content-Type": "application/json" },
    body: JSON.stringify({ itemIds: ids }),
  }); await refresh(request.id);
}

function ManagedRequestCreate({ user, vessels, onClose, onCreated }: {
  user: LoginResult; vessels: Vessel[]; onClose: () => void; onCreated: (id: string) => void;
}) {
  const [draft, setDraft] = useState({
    vesselId: vessels[0]?.id ?? "", requestDate: new Date().toISOString().slice(0, 10),
    contactName: user.fullName, contactEmail: user.email, contactPhone: "",
    preferredPort: "", expectedArrivalDate: "", expectedDepartureDate: "",
    serviceCompany: "", attentionTo: "", subject: "", generalRemarks: "",
  });
  const [error, setError] = useState("");
  return <div className="modal-backdrop"><form className="modal inspection-dialog" onSubmit={async e => {
    e.preventDefault(); setError("");
    const response = await authFetch(`${apiBaseUrl}/service-requests`, user.token, {
      method: "POST", headers: { "Content-Type": "application/json" },
      body: JSON.stringify({ ...draft,
        expectedArrivalDate: draft.expectedArrivalDate || null,
        expectedDepartureDate: draft.expectedDepartureDate || null,
        preferredPort: draft.preferredPort || null }),
    });
    if (!response.ok) { const body = await response.json().catch(() => null);
      setError(body?.detail ?? "Unable to create request."); return; }
    onCreated((await response.json()).id);
  }}><div className="modal-heading"><h2>Create Service Request</h2><button type="button" onClick={onClose}>×</button></div>
    {error && <div className="error">{error}</div>}<div className="form-grid">
      <label>Vessel<select value={draft.vesselId} onChange={e => setDraft({...draft,vesselId:e.target.value})}>
        {vessels.map(x => <option key={x.id} value={x.id}>{x.vesselName}</option>)}</select></label>
      <label>Request date<input type="date" required value={draft.requestDate} onChange={e => setDraft({...draft,requestDate:e.target.value})} /></label>
      <label>Contact name<input required value={draft.contactName} onChange={e => setDraft({...draft,contactName:e.target.value})} /></label>
      <label>Contact email<input type="email" required value={draft.contactEmail} onChange={e => setDraft({...draft,contactEmail:e.target.value})} /></label>
      <label>Preferred port (optional)<input value={draft.preferredPort} onChange={e => setDraft({...draft,preferredPort:e.target.value})} /></label>
      <label>ETA<input type="date" value={draft.expectedArrivalDate} onChange={e => setDraft({...draft,expectedArrivalDate:e.target.value})} /></label>
      <label>ETD<input type="date" value={draft.expectedDepartureDate} onChange={e => setDraft({...draft,expectedDepartureDate:e.target.value})} /></label>
      <label>Service company<input value={draft.serviceCompany} onChange={e => setDraft({...draft,serviceCompany:e.target.value})} /></label>
    </div><label>Subject<input required value={draft.subject} onChange={e => setDraft({...draft,subject:e.target.value})} /></label>
    <label>General remarks<textarea value={draft.generalRemarks} onChange={e => setDraft({...draft,generalRemarks:e.target.value})} /></label>
    <div className="modal-actions"><button type="button" onClick={onClose}>Cancel</button><button className="primary">Create Draft</button></div>
  </form></div>;
}

function ManagedItemSelection({ request, type, token, onClose, onAdded }: {
  request: ManagedServiceRequest; type: "Equipment" | "InspectionFinding"; token: string;
  onClose: () => void; onAdded: () => void;
}) {
  const [options, setOptions] = useState<Array<Record<string, unknown>>>([]);
  const [selected, setSelected] = useState<Set<string>>(new Set());
  const [search, setSearch] = useState("");
  const [serviceDescription, setServiceDescription] = useState("");
  const [error, setError] = useState("");
  useEffect(() => {
    authFetch(`${apiBaseUrl}/service-requests/${request.id}/${type === "Equipment" ? "available-equipment" : "available-findings"}`, token)
      .then(x => x.json()).then(setOptions).catch(() => setError("Unable to load selections."));
  }, []);
  const visible = options.filter(x => JSON.stringify(x).toLowerCase().includes(search.toLowerCase()));
  return <div className="modal-backdrop"><div className="modal selection-dialog">
    <div className="modal-heading"><h2>Select {type === "Equipment" ? "Equipment" : "Inspection Findings"}</h2><button onClick={onClose}>×</button></div>
    {error && <div className="error">{error}</div>}<label>Search<input value={search} onChange={e => setSearch(e.target.value)} /></label>
    <div className="selection-list">{visible.map(x => {
      const id = String(x.id); const title = String(x.equipmentName ?? x.equipmentOrArea);
      return <label key={id}><input type="checkbox" checked={selected.has(id)}
        onChange={e => setSelected(current => { const next = new Set(current);
          e.target.checked ? next.add(id) : next.delete(id); return next; })} />
        <span><strong>{title}</strong><small>{String(x.category ?? x.reportNumber ?? "")} · {String(x.status ?? "")} · {String(x.expiryDate ?? x.exactLocation ?? "")}</small></span>
      </label>;})}</div>
    <label>Required service<input value={serviceDescription}
      onChange={e => setServiceDescription(e.target.value)}
      placeholder="Describe the required service" /></label>
    <div className="modal-actions"><button onClick={onClose}>Cancel</button><button className="primary"
      disabled={selected.size === 0 || !serviceDescription.trim()} onClick={async () => {
        const response = await authFetch(`${apiBaseUrl}/service-requests/${request.id}/items`, token, {
          method: "POST", headers: { "Content-Type": "application/json" },
          body: JSON.stringify({ itemType: type, sourceIds: [...selected],
            serviceDescription: serviceDescription.trim(), allowDuplicate: false }),
        });
        if (!response.ok) { const body = await response.json().catch(() => null);
          setError(body?.detail ?? "Unable to add selections."); return; }
        onAdded();
      }}>Add Selected</button></div>
  </div></div>;
}

function InspectionReportsPage({
  user, vessels,
}: {
  user: LoginResult; vessels: Vessel[];
}) {
  const [reports, setReports] = useState<InspectionReportSummary[]>([]);
  const [report, setReport] = useState<InspectionReport | null>(null);
  const [pdfs, setPdfs] = useState<InspectionPdf[]>([]);
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState("");
  const [createOpen, setCreateOpen] = useState(false);
  const [editingFinding, setEditingFinding] = useState<InspectionFinding | null>(null);

  async function request(url: string, init: RequestInit = {}) {
    const response = await authFetch(`${apiBaseUrl}${url}`, user.token, init);
    if (!response.ok) {
      const body = await response.json().catch(() => null);
      throw new Error(body?.detail ?? body?.title ?? "Request failed.");
    }
    return response;
  }
  async function loadList() {
    setBusy(true); setError("");
    try {
      setReports(await (await request("/inspection-reports")).json());
    } catch (reason) { setError((reason as Error).message); }
    finally { setBusy(false); }
  }
  async function openReport(id: string, push = true) {
    setBusy(true); setError("");
    try {
      const [detail, versions] = await Promise.all([
        request(`/inspection-reports/${id}`).then(x => x.json()),
        request(`/inspection-reports/${id}/pdf-versions`).then(x => x.json()),
      ]);
      setReport(detail); setPdfs(versions);
      if (push) window.history.pushState({ inspectionReportId: id }, "", `#inspection-reports/${id}`);
    } catch (reason) { setError((reason as Error).message); }
    finally { setBusy(false); }
  }
  useEffect(() => { void loadList(); }, []);
  useEffect(() => {
    const pop = (event: PopStateEvent) => {
      const id = typeof event.state?.inspectionReportId === "string"
        ? event.state.inspectionReportId : null;
      if (id) void openReport(id, false); else { setReport(null); void loadList(); }
    };
    window.addEventListener("popstate", pop);
    return () => window.removeEventListener("popstate", pop);
  }, []);
  function back() {
    if (window.history.state?.inspectionReportId) window.history.back();
    else { setReport(null); void loadList(); }
  }
  async function download(url: string, name: string) {
    try {
      const response = await request(url);
      const objectUrl = URL.createObjectURL(await response.blob());
      const anchor = document.createElement("a");
      anchor.href = objectUrl; anchor.download = name; anchor.click();
      URL.revokeObjectURL(objectUrl);
    } catch (reason) { setError((reason as Error).message); }
  }
  async function action(url: string, body?: unknown) {
    setBusy(true); setError("");
    try {
      await request(url, {
        method: "POST",
        headers: body ? { "Content-Type": "application/json" } : undefined,
        body: body ? JSON.stringify(body) : undefined,
      });
      if (report) await openReport(report.id, false); else await loadList();
    } catch (reason) { setError((reason as Error).message); }
    finally { setBusy(false); }
  }

  if (!report) return (
    <main className="workspace inspection-reports-page">
      <div className="workspace-heading">
        <div><h1>Inspection Reports</h1><p className="muted">Vessel inspection reports and finalized PDF versions.</p></div>
        {user.role !== "Crew" &&
          <button className="primary" onClick={() => setCreateOpen(true)}>Create Report</button>}
      </div>
      {error && <div className="error">{error}</div>}
      {busy ? <div className="loading">Loading inspection reports...</div> :
        reports.length === 0 ? <div className="empty-state">No inspection reports available.</div> :
        <div className="table-scroll"><table className="equipment-table inspection-report-table">
          <thead><tr><th>Report number</th><th>Vessel</th><th>Inspection date</th>
            <th>Inspector</th><th>Type</th><th>Findings</th><th>Open</th>
            <th>Created</th><th>Actions</th></tr></thead>
          <tbody>{reports.map(item => <tr key={item.id}>
            <td><strong>{item.reportNumber}</strong><small>{item.state}</small></td>
            <td>{item.vesselName}</td><td>{formatDate(item.inspectionDate)}</td>
            <td>{item.inspectorName}</td><td>{item.inspectionType === "DeckAndEngine" ? "Deck and Engine" : item.inspectionType}</td>
            <td>{item.findingCount}</td><td>{item.openFindingCount}</td>
            <td>{formatDateTime(item.createdUtc)}</td><td className="row-actions">
              <button onClick={() => void openReport(item.id)}>Open</button>
              {item.state === "Finalized" && <button onClick={async () => {
                await action(`/inspection-reports/${item.id}/generate-pdf`);
                await openReport(item.id);
              }}>Generate PDF</button>}
            </td>
          </tr>)}</tbody>
        </table></div>}
      {createOpen && <InspectionCreateDialog vessels={vessels} user={user}
        onClose={() => setCreateOpen(false)}
        onCreated={id => { setCreateOpen(false); void openReport(id); }} />}
    </main>
  );

  const editable = report.state === "Draft" && user.role !== "Crew";
  return (
    <main className="workspace inspection-reports-page">
      <div className="workspace-heading">
        <div><button className="back-button" onClick={back}>← Inspection Reports</button>
          <h1>{report.reportNumber}</h1>
          <p className="muted">{report.vesselName} · IMO {report.imoNumber} · {report.state}</p></div>
        <div className="workspace-actions">
          {editable && <button onClick={async () => {
            const inspectionDate = window.prompt(
              "Inspection date (YYYY-MM-DD):", report.inspectionDate);
            if (!inspectionDate) return;
            const inspectorName = window.prompt("Inspector name:", report.inspectorName);
            if (!inspectorName) return;
            const typeInput = window.prompt(
              "Inspection type (Deck, Engine, or DeckAndEngine):",
              report.inspectionType);
            if (!typeInput ||
                !["Deck", "Engine", "DeckAndEngine"].includes(typeInput)) {
              setError("Inspection type must be Deck, Engine, or DeckAndEngine.");
              return;
            }
            const generalRemarks = window.prompt("General remarks:", report.generalRemarks ?? "");
            if (generalRemarks === null) return;
            await request(`/inspection-reports/${report.id}`, {
              method: "PUT", headers: { "Content-Type": "application/json" },
              body: JSON.stringify({
                inspectionDate, inspectorName,
                inspectionType: typeInput, generalRemarks,
              }),
            });
            await openReport(report.id, false);
          }}>Edit Header</button>}
          {editable && <button onClick={() => setEditingFinding({
            id: "", sequenceNumber: report.findings.length + 1, department: "Deck",
            equipmentOrArea: "", exactLocation: "", description: "", priority: "Normal",
            status: "Open", inspectorRemarks: "", photos: [],
          })}>Add Finding</button>}
          {editable && <button className="primary"
            onClick={() => void action(`/inspection-reports/${report.id}/finalize`)}>Finalize Report</button>}
          {report.state === "Finalized" && <button className="primary"
            onClick={() => void action(`/inspection-reports/${report.id}/generate-pdf`)}>Generate PDF</button>}
          {report.state === "Finalized" && user.role === "OfficeAdmin" &&
            <button onClick={() => {
              const reason = window.prompt("Reason for reopening:");
              if (reason) void action(`/inspection-reports/${report.id}/reopen`, { reason });
            }}>Reopen</button>}
        </div>
      </div>
      {error && <div className="error">{error}</div>}
      <section className="details-card inspection-header">
        <Detail label="Inspection date" value={formatDate(report.inspectionDate)} />
        <Detail label="Inspector" value={report.inspectorName} />
        <Detail label="Inspection type" value={report.inspectionType === "DeckAndEngine" ? "Deck and Engine" : report.inspectionType} />
        <Detail label="Created" value={formatDateTime(report.createdUtc)} />
        <Detail label="Updated" value={formatDateTime(report.updatedUtc)} />
        <Detail label="General remarks" value={report.generalRemarks ?? "—"} />
      </section>
      <section className="dashboard-section">
        <div className="dashboard-section-heading"><h2>Findings</h2>
          <span className="muted">{report.findings.length} total · {report.findings.filter(x => x.status === "Open").length} open</span></div>
        {report.findings.length === 0 ? <div className="empty-state">No findings recorded.</div> :
          <div className="inspection-findings">{report.findings.map((finding, index) =>
            <article className={`inspection-finding priority-${finding.priority.toLowerCase()}`} key={finding.id}>
              <div className="inspection-finding-heading">
                <h3>Finding {finding.sequenceNumber}: {finding.equipmentOrArea}</h3>
                <div><span className="status">{finding.priority}</span> <span className="status">{finding.status}</span></div>
              </div>
              <p><strong>{finding.department}</strong>{finding.exactLocation ? ` · ${finding.exactLocation}` : ""}</p>
              <p>{finding.description}</p>
              {finding.inspectorRemarks && <p className="muted">Inspector remarks: {finding.inspectorRemarks}</p>}
              <div className="inspection-photos">{finding.photos.map(photo =>
                <figure key={photo.id}>
                  <AuthorizedImage photo={photo} token={user.token}
                    onClick={() => void download(`/inspection-photos/${photo.id}/download`, photo.originalFileName)} />
                  <figcaption>{photo.caption ?? photo.originalFileName}</figcaption>
                  {editable && <div className="row-actions">
                    <button onClick={async () => {
                      const caption = window.prompt("Photo caption:", photo.caption ?? "");
                      if (caption === null) return;
                      await request(`/inspection-photos/${photo.id}`, {
                        method: "PUT", headers: { "Content-Type": "application/json" },
                        body: JSON.stringify({ caption }),
                      });
                      await openReport(report.id, false);
                    }}>Caption</button>
                    <button className="danger" onClick={async () => {
                      await request(`/inspection-photos/${photo.id}`, { method: "DELETE" });
                      await openReport(report.id, false);
                    }}>Remove</button>
                  </div>}
                </figure>)}</div>
              {editable && <div className="row-actions">
                <button onClick={() => setEditingFinding(finding)}>Edit</button>
                <label className="button-like">Add Photos<input className="hidden-file-input"
                  type="file" accept="image/jpeg,image/png" multiple onChange={async event => {
                    for (const file of Array.from(event.target.files ?? [])) {
                      const data = new FormData(); data.append("file", file);
                      await request(`/inspection-findings/${finding.id}/photos`, { method: "POST", body: data });
                    }
                    await openReport(report.id, false);
                  }} /></label>
                <button disabled={index === 0} onClick={() => void reorder(report, finding.id, -1, request, openReport)}>↑</button>
                <button disabled={index === report.findings.length - 1} onClick={() => void reorder(report, finding.id, 1, request, openReport)}>↓</button>
                <button className="danger" onClick={async () => {
                  if (!window.confirm("Remove this finding?")) return;
                  await request(`/inspection-findings/${finding.id}`, { method: "DELETE" });
                  await openReport(report.id, false);
                }}>Remove</button>
              </div>}
            </article>)}</div>}
      </section>
      <section className="dashboard-section">
        <div className="dashboard-section-heading"><h2>PDF Versions</h2></div>
        {pdfs.length === 0 ? <div className="empty-state">No PDF generated.</div> :
          <div className="document-list">{pdfs.map(pdf => <div className="document-row" key={pdf.id}>
            <div><strong>{pdf.fileName}</strong><small>Version {pdf.versionNumber} · {formatDateTime(pdf.generatedUtc)} · {pdf.generatedBy}</small></div>
            <div>{pdf.isCurrent && <span className="document-badge current">Current</span>}
              {!pdf.isCurrent && <span className="document-badge archived">Archive</span>}
              <button onClick={() => void download(`/inspection-report-pdfs/${pdf.id}/download`, pdf.fileName)}>Open</button></div>
          </div>)}</div>}
      </section>
      {editingFinding && <InspectionFindingDialog reportId={report.id}
        finding={editingFinding} token={user.token}
        onClose={() => setEditingFinding(null)}
        onSaved={() => { setEditingFinding(null); void openReport(report.id, false); }} />}
    </main>
  );
}

async function reorder(report: InspectionReport, id: string, offset: number,
  request: (url: string, init?: RequestInit) => Promise<Response>,
  refresh: (id: string, push?: boolean) => Promise<void>) {
  const ids = report.findings.map(x => x.id);
  const index = ids.indexOf(id); const target = index + offset;
  [ids[index], ids[target]] = [ids[target], ids[index]];
  await request(`/inspection-reports/${report.id}/findings/reorder`, {
    method: "POST", headers: { "Content-Type": "application/json" },
    body: JSON.stringify({ findingIds: ids }),
  });
  await refresh(report.id, false);
}

function AuthorizedImage({ photo, token, onClick }: {
  photo: InspectionPhoto; token: string; onClick: () => void;
}) {
  const [source, setSource] = useState("");
  useEffect(() => {
    let active = true;
    let objectUrl = "";
    authFetch(`${apiBaseUrl}/inspection-photos/${photo.id}/download`, token)
      .then(response => response.ok ? response.blob() : Promise.reject())
      .then(blob => {
        objectUrl = URL.createObjectURL(blob);
        if (active) setSource(objectUrl);
      }).catch(() => {});
    return () => {
      active = false;
      if (objectUrl) URL.revokeObjectURL(objectUrl);
    };
  }, [photo.id, token]);
  return source
    ? <img src={source} alt={photo.caption ?? photo.originalFileName} onClick={onClick} />
    : <div className="photo-loading">Loading photo…</div>;
}

function InspectionCreateDialog({ vessels, user, onClose, onCreated }: {
  vessels: Vessel[]; user: LoginResult; onClose: () => void;
  onCreated: (id: string) => void;
}) {
  const [draft, setDraft] = useState({
    vesselId: vessels[0]?.id ?? "", inspectionDate: new Date().toISOString().slice(0, 10),
    inspectorName: user.fullName, inspectionType: "Deck" as InspectionReport["inspectionType"],
    generalRemarks: "",
  });
  const [error, setError] = useState("");
  return <div className="modal-backdrop"><form className="modal inspection-dialog" onSubmit={async event => {
    event.preventDefault(); setError("");
    const response = await authFetch(`${apiBaseUrl}/inspection-reports`, user.token, {
      method: "POST", headers: { "Content-Type": "application/json" },
      body: JSON.stringify(draft),
    });
    if (!response.ok) { const body = await response.json().catch(() => null); setError(body?.detail ?? "Unable to create report."); return; }
    onCreated((await response.json()).id);
  }}>
    <div className="modal-heading"><h2>Create Inspection Report</h2><button type="button" onClick={onClose}>×</button></div>
    {error && <div className="error">{error}</div>}
    <label>Vessel<select required value={draft.vesselId} onChange={e => setDraft({ ...draft, vesselId: e.target.value })}>
      {vessels.map(x => <option key={x.id} value={x.id}>{x.vesselName}</option>)}</select></label>
    <label>Inspection date<input required type="date" value={draft.inspectionDate} onChange={e => setDraft({ ...draft, inspectionDate: e.target.value })} /></label>
    <label>Inspector<input required value={draft.inspectorName} onChange={e => setDraft({ ...draft, inspectorName: e.target.value })} /></label>
    <label>Inspection type<select value={draft.inspectionType} onChange={e => setDraft({ ...draft, inspectionType: e.target.value as InspectionReport["inspectionType"] })}>
      <option value="Deck">Deck</option><option value="Engine">Engine</option><option value="DeckAndEngine">Deck and Engine</option></select></label>
    <label>General remarks<textarea value={draft.generalRemarks} onChange={e => setDraft({ ...draft, generalRemarks: e.target.value })} /></label>
    <div className="modal-actions"><button type="button" onClick={onClose}>Cancel</button><button className="primary">Create Draft</button></div>
  </form></div>;
}

function InspectionFindingDialog({ reportId, finding, token, onClose, onSaved }: {
  reportId: string; finding: InspectionFinding; token: string;
  onClose: () => void; onSaved: () => void;
}) {
  const [draft, setDraft] = useState(finding);
  const [error, setError] = useState("");
  return <div className="modal-backdrop"><form className="modal inspection-dialog" onSubmit={async event => {
    event.preventDefault(); setError("");
    const url = finding.id ? `/inspection-findings/${finding.id}` : `/inspection-reports/${reportId}/findings`;
    const response = await authFetch(`${apiBaseUrl}${url}`, token, {
      method: finding.id ? "PUT" : "POST", headers: { "Content-Type": "application/json" },
      body: JSON.stringify(draft),
    });
    if (!response.ok) { const body = await response.json().catch(() => null); setError(body?.detail ?? "Unable to save finding."); return; }
    onSaved();
  }}>
    <div className="modal-heading"><h2>{finding.id ? "Edit" : "Add"} Finding</h2><button type="button" onClick={onClose}>×</button></div>
    {error && <div className="error">{error}</div>}
    <div className="form-grid">
      <label>Department<select value={draft.department} onChange={e => setDraft({ ...draft, department: e.target.value as InspectionFinding["department"] })}><option>Deck</option><option>Engine</option></select></label>
      <label>Priority<select value={draft.priority} onChange={e => setDraft({ ...draft, priority: e.target.value as InspectionFinding["priority"] })}><option>Critical</option><option>Urgent</option><option>Normal</option></select></label>
      <label>Equipment or area<input required value={draft.equipmentOrArea} onChange={e => setDraft({ ...draft, equipmentOrArea: e.target.value })} /></label>
      <label>Exact location<input value={draft.exactLocation ?? ""} onChange={e => setDraft({ ...draft, exactLocation: e.target.value })} /></label>
      <label>Status<select value={draft.status} onChange={e => setDraft({ ...draft, status: e.target.value as InspectionFinding["status"] })}><option>Open</option><option>Closed</option></select></label>
    </div>
    <label>Description<textarea required rows={4} value={draft.description} onChange={e => setDraft({ ...draft, description: e.target.value })} /></label>
    <label>Inspector remarks<textarea value={draft.inspectorRemarks ?? ""} onChange={e => setDraft({ ...draft, inspectorRemarks: e.target.value })} /></label>
    <div className="modal-actions"><button type="button" onClick={onClose}>Cancel</button><button className="primary">Save Finding</button></div>
  </form></div>;
}

function Detail({ label, value }: { label: string; value: string }) {
  return (
    <div className="detail">
      <span>{label}</span>
      <strong>{value}</strong>
    </div>
  );
}

ReactDOM.createRoot(document.getElementById("root")!).render(
  <React.StrictMode><App /></React.StrictMode>,
);
