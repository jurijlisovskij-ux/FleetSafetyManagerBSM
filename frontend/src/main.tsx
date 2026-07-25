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
  | "medical"
  | "administration"
  | "synchronization"
  | "profile";

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

function App() {
  const [email, setEmail] = useState("superintendent@fsm.local");
  const [password, setPassword] = useState("Super123!");
  const [user, setUser] = useState<LoginResult | null>(null);
  const [vessels, setVessels] = useState<Vessel[]>([]);
  const [dashboard, setDashboard] = useState<DashboardData | null>(null);
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
    if (view === "administration")
      setShowUserAdministration(true);
  }

  function signOut() {
    window.history.replaceState(
      {},
      "",
      `${window.location.pathname}${window.location.search}`,
    );
    setUser(null);
    setVessels([]);
    setDashboard(null);
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
        <div><strong>Fleet Safety Manager</strong><span>Version RC18</span></div>
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
        <NavigationPage
          title="Administration"
          description="User administration is open."
        />
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
