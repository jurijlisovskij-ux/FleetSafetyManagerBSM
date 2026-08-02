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
  edition: "Office" | "Ship";
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
  synchronization: SynchronizationStatus[];
};

type SynchronizationStatus = {
  vesselId: string;
  vesselName: string;
  imoNumber: string;
  lastSynchronizationUtc?: string;
  status: "Success" | "Warning" | "Failed";
  pendingChanges: number;
  automaticIntervalHours: 0 | 6 | 12 | 24;
  currentStage: string;
  configured: boolean;
  remoteBaseUrl?: string;
  lastError?: string;
};

type SynchronizationStatusResponse = {
  edition: "Office" | "Ship";
  applicationVersion: string;
  vessels: SynchronizationStatus[];
};

type SynchronizationLog = {
  id: string;
  dateUtc: string;
  vesselId: string;
  vessel: string;
  direction: string;
  durationSeconds: number;
  uploadedItems: number;
  downloadedItems: number;
  result: "Success" | "Warning" | "Failed";
  errorMessage?: string;
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

function formatDate(value?: string) {
  if (!value) return "—";
  return new Intl.DateTimeFormat("en-GB", {
    day: "2-digit",
    month: "short",
    year: "numeric",
  }).format(new Date(`${value}T00:00:00`));
}

function formatSynchronizationDateTime(value?: string) {
  if (!value) return { date: "Never", time: "—" };
  const parsed = new Date(value);
  return {
    date: new Intl.DateTimeFormat("en-GB", {
      day: "2-digit",
      month: "short",
      year: "numeric",
    }).format(parsed),
    time: new Intl.DateTimeFormat("en-GB", {
      hour: "2-digit",
      minute: "2-digit",
      second: "2-digit",
    }).format(parsed),
  };
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
  const [showCreateVessel, setShowCreateVessel] =
    useState(false);
  const [showUserAdministration, setShowUserAdministration] =
    useState(false);
  const [showCopyEquipment, setShowCopyEquipment] =
    useState(false);
  const [showSynchronization, setShowSynchronization] =
    useState(false);
  const [collapsed, setCollapsed] =
    useState<Set<Equipment["category"]>>(new Set());
  const [error, setError] = useState("");
  const [busy, setBusy] = useState(false);

  async function refreshFleetData() {
    if (!user) return;

    const [vesselsResponse, dashboardResponse] = await Promise.all([
      authFetch(`${apiBaseUrl}/vessels`, user.token, {
        cache: "no-store",
      }),
      authFetch(`${apiBaseUrl}/dashboard`, user.token, {
        cache: "no-store",
      }),
    ]);

    if (!vesselsResponse.ok)
      throw new Error("Unable to load vessels.");
    if (!dashboardResponse.ok)
      throw new Error("Unable to load dashboard.");

    const vesselItems: Vessel[] = await vesselsResponse.json();
    const dashboardData: DashboardData =
      await dashboardResponse.json();

    setVessels(vesselItems);
    setDashboard(dashboardData);

    if (user.edition === "Ship" && vesselItems.length === 1) {
      setCurrentVessel(vesselItems[0]);
    }
  }

  useEffect(() => {
    void refreshFleetData()
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
      })
      .catch(error => setError((error as Error).message))
      .finally(() => setBusy(false));
  }, [user, currentVessel]);

  const groups = useMemo(
    () =>
      categories.map(category => ({
        category,
        items: equipment.filter(item => item.category === category),
      })),
    [equipment],
  );

  const singleVesselDashboard =
    (dashboard?.vesselRows.length ?? 0) === 1;

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
    <div className={`application ${user.edition === "Ship" ? "ship-edition" : "office-edition"}`}>
      <header className="topbar">
        <div><strong>Fleet Safety Manager</strong><span>Version RC20</span></div>
        <div className="session">
          <span>{user.fullName} · {user.role}</span>
          <button onClick={() => setShowSynchronization(true)}>
            Synchronize
          </button>
          {currentVessel && user.edition !== "Ship" &&
            <button
              onClick={() => {
                setCurrentVessel(null);
                setEquipment([]);
                setSelected(null);
                setSelectedForRequest(new Set());
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
            setUser(null);
            setVessels([]);
            setDashboard(null);
            setDashboardFilter("All");
            setCurrentVessel(null);
            setEquipment([]);
            setSelected(null);
            setSelectedForRequest(new Set());
            setShowServiceRequest(false);
            setShowInspectionList(false);
            setShowAddEquipment(false);
            setShowVesselProfile(false);
            setShowCreateVessel(false);
            setShowUserAdministration(false);
            setShowCopyEquipment(false);
            setShowSynchronization(false);
          }}>Sign Out</button>
        </div>
      </header>

      {!currentVessel ? (
        <main className="dashboard-page">
          <div className="dashboard-heading">
            <div>
              <h1>Dashboard</h1>
              <p className="muted">Fleet equipment status.</p>
            </div>
            {user.role === "OfficeAdmin" &&
              <button
                className="primary"
                onClick={() => setShowCreateVessel(true)}
              >
                Add Vessel
              </button>}
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
                    {!singleVesselDashboard &&
                      <thead>
                        <tr>
                          <th>Vessel</th>
                          <th>Equipment</th>
                          <th>OK</th>
                          <th>Soon</th>
                          <th>Overdue</th>
                        </tr>
                      </thead>}
                    <tbody>
                      {(dashboard?.vesselRows ?? []).map(row => (
                        <tr
                          key={row.id}
                          onClick={() => {
                            const vessel = vessels.find(item => item.id === row.id);
                            if (vessel) setCurrentVessel(vessel);
                          }}
                        >
                          <td>
                            <strong>{row.vesselName}</strong>
                            <small>IMO {row.imoNumber}</small>
                            {singleVesselDashboard &&
                              <small>Equipment: {row.equipment}</small>}
                          </td>
                          {!singleVesselDashboard && <>
                            <td>{row.equipment}</td>
                            <td className="status-ok-text">{row.ok}</td>
                            <td className="status-soon-text">{row.soon}</td>
                            <td className="status-overdue-text">{row.overdue}</td>
                          </>}
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
                    {!singleVesselDashboard &&
                      <th>Vessel</th>}
                    <th>Equipment</th>
                    <th>Category</th>
                    <th>Expiry Date</th>
                    {!singleVesselDashboard &&
                      <th>Days Left</th>}
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
                          if (vessel) setCurrentVessel(vessel);
                        }}
                      >
                        {!singleVesselDashboard &&
                          <td>{item.vesselName}</td>}
                        <td>{item.equipmentName}</td>
                        <td>{item.category}</td>
                        <td>{formatDate(item.expiryDate)}</td>
                        {!singleVesselDashboard &&
                          <td>{item.daysLeft}</td>}
                        <td><span className={`status ${item.status.toLowerCase()}`}>{item.status}</span></td>
                      </tr>
                    ))}
                </tbody>
              </table>
            </div>
          </section>

          {user.edition === "Office" &&
            <section className="dashboard-section">
              <div className="dashboard-section-heading">
                <h2>Synchronization Status</h2>
                <button onClick={() => setShowSynchronization(true)}>
                  Synchronize
                </button>
              </div>
              <div className="dashboard-table-wrap">
                <table className="dashboard-table synchronization-table">
                  <thead>
                    <tr>
                      <th>Vessel</th>
                      <th>Last Sync</th>
                      <th>Pending Changes</th>
                      <th>Status</th>
                    </tr>
                  </thead>
                  <tbody>
                    {(dashboard?.synchronization ?? []).map(item => {
                      const lastSync = formatSynchronizationDateTime(
                        item.lastSynchronizationUtc,
                      );
                      return (
                        <tr key={item.vesselId}>
                          <td>
                            <strong>{item.vesselName}</strong>
                            <small>IMO {item.imoNumber}</small>
                          </td>
                          <td>
                            {lastSync.date}
                            <small>{lastSync.time}</small>
                          </td>
                          <td>{item.pendingChanges}</td>
                          <td>
                            <span className={`sync-state ${item.status.toLowerCase()}`}>
                              {item.status}
                            </span>
                          </td>
                        </tr>
                      );
                    })}
                  </tbody>
                </table>
              </div>
            </section>}
        </main>
      ) : (
        <main className="workspace">
          <div className="workspace-heading">
            <div>
              <button
                className="back-button"
                onClick={() => {
                  setCurrentVessel(null);
                  setEquipment([]);
                  setSelected(null);
                  setSelectedForRequest(new Set());
                  setShowServiceRequest(false);
            setShowInspectionList(false);
            setShowAddEquipment(false);
            setShowVesselProfile(false);
            setShowUserAdministration(false);
            setShowCopyEquipment(false);
                }}
              >
                ← Assigned Vessels
              </button>
              <h1>{currentVessel.vesselName}</h1>
              <p className="muted">
                IMO {currentVessel.imoNumber}
                {currentVessel.flag ? ` · ${currentVessel.flag}` : ""}
              </p>
            </div>
            <div className="workspace-actions">
              <span className="muted">Equipment Register</span>
              <button
                type="button"
                onClick={() => setShowSynchronization(true)}
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
            <div className="workspace-grid">
              <section className="register">
                <div className="table-scroll">
                  <table>
                    <thead>
                      <tr>
                        <th className="select-column">Select</th>
                        <th>Equipment</th>
                        <th>Maker</th>
                        <th>Model</th>
                        <th>Qty</th>
                        <th>Inspection By</th>
                        <th>Expiry Date</th>
                        <th>Status</th>
                      </tr>
                    </thead>
                    <tbody>
                      {groups.map(group => (
                        <React.Fragment key={group.category}>
                          <tr
                            className="category-row"
                            onClick={() =>
                              setCollapsed(current => {
                                const next = new Set(current);
                                next.has(group.category)
                                  ? next.delete(group.category)
                                  : next.add(group.category);
                                return next;
                              })
                            }
                          >
                            <td colSpan={8}>
                              {collapsed.has(group.category) ? "▶" : "▼"}{" "}
                              {group.category}
                            </td>
                          </tr>
                          {!collapsed.has(group.category) &&
                            group.items.map(item => (
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
                                <td>{item.equipmentName}</td>
                                <td>{item.maker || "—"}</td>
                                <td>{item.model || "—"}</td>
                                <td>{item.quantity}</td>
                                <td>{item.inspectionBy}</td>
                                <td>{formatDate(item.expiryDate)}</td>
                                <td>
                                  <span className={`status status-${item.status.toLowerCase()}`}>
                                    {item.status}
                                  </span>
                                </td>
                              </tr>
                            ))}
                        </React.Fragment>
                      ))}
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
          )}
        </main>
      )}






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
          onClose={() =>
            setShowUserAdministration(false)}
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

      {showCreateVessel &&
        user.role === "OfficeAdmin" &&
        <CreateVesselDialog
          token={user.token}
          onClose={() => setShowCreateVessel(false)}
          onCreated={async () => {
            await refreshFleetData();
            setShowCreateVessel(false);
          }}
        />}

      {showSynchronization &&
        <SynchronizationDialog
          token={user.token}
          userRole={user.role}
          initialVesselId={currentVessel?.id}
          onClose={() => setShowSynchronization(false)}
          onStatusChanged={items => {
            setDashboard(current => current
              ? { ...current, synchronization: items }
              : current);
          }}
        />}

    </div>
  );
}







function SynchronizationDialog({
  token,
  userRole,
  initialVesselId,
  onClose,
  onStatusChanged,
}: {
  token: string;
  userRole: LoginResult["role"];
  initialVesselId?: string;
  onClose: () => void;
  onStatusChanged: (items: SynchronizationStatus[]) => void;
}) {
  const [statusResponse, setStatusResponse] =
    useState<SynchronizationStatusResponse | null>(null);
  const [logs, setLogs] = useState<SynchronizationLog[]>([]);
  const [selectedVesselId, setSelectedVesselId] =
    useState(initialVesselId ?? "");
  const [intervalHours, setIntervalHours] =
    useState<0 | 6 | 12 | 24>(24);
  const [remoteBaseUrl, setRemoteBaseUrl] = useState("");
  const [phase, setPhase] = useState("");
  const [generatedToken, setGeneratedToken] = useState("");
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState("");

  const selected = statusResponse?.vessels.find(
    item => item.vesselId === selectedVesselId,
  );
  const lastSync = formatSynchronizationDateTime(
    selected?.lastSynchronizationUtc,
  );
  const phases = [
    "Checking connection...",
    "Uploading changes...",
    "Downloading updates...",
    "Synchronization completed.",
  ];
  const currentPhaseIndex = phases.indexOf(phase);

  useEffect(() => {
    void refresh();
  }, []);

  useEffect(() => {
    if (!selected) return;
    setIntervalHours(selected.automaticIntervalHours);
    setRemoteBaseUrl(selected.remoteBaseUrl ?? "");
  }, [selected?.vesselId]);

  async function loadStatus(notify = true) {
    const response = await authFetch(
      `${apiBaseUrl}/synchronization/status`,
      token,
    );
    if (!response.ok)
      throw new Error("Unable to load synchronization status.");
    const body: SynchronizationStatusResponse = await response.json();
    setStatusResponse(body);
    if (!selectedVesselId && body.vessels.length > 0)
      setSelectedVesselId(initialVesselId ?? body.vessels[0].vesselId);
    if (notify) onStatusChanged(body.vessels);
    return body;
  }

  async function loadLogs() {
    const response = await authFetch(
      `${apiBaseUrl}/synchronization/logs`,
      token,
    );
    if (!response.ok)
      throw new Error("Unable to load the synchronization log.");
    setLogs(await response.json());
  }

  async function refresh() {
    setError("");
    try {
      await Promise.all([loadStatus(), loadLogs()]);
    } catch (exception) {
      setError((exception as Error).message);
    }
  }

  async function runSynchronization() {
    if (!selectedVesselId) return;
    setBusy(true);
    setError("");
    setPhase("Checking connection...");
    const poll = window.setInterval(async () => {
      try {
        const current = await loadStatus(false);
        const vesselStatus = current.vessels.find(
          item => item.vesselId === selectedVesselId,
        );
        if (vesselStatus?.currentStage)
          setPhase(vesselStatus.currentStage);
      } catch {
        // The active request reports the final error.
      }
    }, 750);

    try {
      const response = await authFetch(
        `${apiBaseUrl}/synchronization/run/${selectedVesselId}`,
        token,
        { method: "POST" },
      );
      if (!response.ok) {
        const body = await response.json().catch(() => null);
        throw new Error(body?.message ?? "Synchronization failed.");
      }
      setPhase("Synchronization completed.");
      await refresh();
    } catch (exception) {
      setPhase("Failed");
      setError((exception as Error).message);
      await loadStatus().catch(() => undefined);
      await loadLogs().catch(() => undefined);
    } finally {
      window.clearInterval(poll);
      setBusy(false);
    }
  }

  async function saveSettings() {
    if (!selectedVesselId) return;
    setBusy(true);
    setError("");
    try {
      const response = await authFetch(
        `${apiBaseUrl}/synchronization/settings/${selectedVesselId}`,
        token,
        {
          method: "PUT",
          headers: { "Content-Type": "application/json" },
          body: JSON.stringify({
            automaticIntervalHours: intervalHours,
            remoteBaseUrl: remoteBaseUrl.trim() || null,
          }),
        },
      );
      if (!response.ok) {
        const body = await response.json().catch(() => null);
        throw new Error(body?.message ?? "Unable to save synchronization settings.");
      }
      await loadStatus();
    } catch (exception) {
      setError((exception as Error).message);
    } finally {
      setBusy(false);
    }
  }

  async function rotateToken() {
    if (!selectedVesselId ||
        !window.confirm("Generate a new vessel synchronization token? The previous token will stop working.")) {
      return;
    }
    setBusy(true);
    setError("");
    setGeneratedToken("");
    try {
      const response = await authFetch(
        `${apiBaseUrl}/synchronization/token/${selectedVesselId}`,
        token,
        { method: "POST" },
      );
      if (!response.ok) {
        const body = await response.json().catch(() => null);
        throw new Error(body?.message ?? "Unable to generate the vessel token.");
      }
      const body = await response.json();
      setGeneratedToken(body.token);
      await loadStatus();
    } catch (exception) {
      setError((exception as Error).message);
    } finally {
      setBusy(false);
    }
  }

  const canRun = Boolean(
    selected?.configured &&
    (statusResponse?.edition === "Ship" || selected?.remoteBaseUrl),
  );

  return (
    <div className="modal">
      <div className="synchronization-dialog">
        <div className="service-request-header">
          <div>
            <h2>Synchronization</h2>
            <p>
              {statusResponse?.edition ?? "—"} Edition · {statusResponse?.applicationVersion ?? "RC20"}
            </p>
          </div>
          <button type="button" onClick={onClose}>Close</button>
        </div>

        {error && <div className="error">{error}</div>}

        <div className="synchronization-vessel-tabs">
          {(statusResponse?.vessels ?? []).map(item => (
            <button
              key={item.vesselId}
              className={item.vesselId === selectedVesselId ? "selected" : ""}
              onClick={() => setSelectedVesselId(item.vesselId)}
            >
              {item.vesselName}
            </button>
          ))}
        </div>

        {selected && <>
          <div className="synchronization-summary">
            <div>
              <span>Last synchronization:</span>
              <strong>{lastSync.date}</strong>
              <small>{lastSync.time}</small>
            </div>
            <div>
              <span>Status:</span>
              <strong className={`sync-state ${selected.status.toLowerCase()}`}>
                {selected.status}
              </strong>
              {selected.lastError && <small>{selected.lastError}</small>}
            </div>
            <div>
              <span>Pending changes:</span>
              <strong>{selected.pendingChanges}</strong>
              <small>{selected.configured ? "Connection configured" : "Configuration required"}</small>
            </div>
          </div>

          <div className="synchronization-progress" aria-live="polite">
            {phases.map((item, index) => (
              <div
                key={item}
                className={
                  item === phase
                    ? "active"
                    : currentPhaseIndex > index
                      ? "complete"
                      : ""
                }
              >
                <span>{currentPhaseIndex > index ? "✓" : index + 1}</span>
                {item}
              </div>
            ))}
            {phase === "Failed" &&
              <div className="failed"><span>!</span>Synchronization failed.</div>}
          </div>

          <div className="synchronization-actions">
            <button
              className="primary"
              disabled={busy || !canRun}
              title={!canRun ? "Configure an HTTPS endpoint and vessel token first." : ""}
              onClick={runSynchronization}
            >
              {busy ? "Synchronizing..." : "Synchronize"}
            </button>
            <button disabled={busy} onClick={refresh}>Refresh</button>
          </div>

          {userRole === "OfficeAdmin" &&
            <section className="synchronization-settings">
              <h3>System Administrator Settings</h3>
              <label>
                Automatic synchronization
                <select
                  value={intervalHours}
                  onChange={event => setIntervalHours(
                    Number(event.target.value) as 0 | 6 | 12 | 24,
                  )}
                >
                  <option value={6}>Every 6 hours</option>
                  <option value={12}>Every 12 hours</option>
                  <option value={24}>Every 24 hours</option>
                  <option value={0}>Manual only</option>
                </select>
              </label>
              <label>
                Remote Ship HTTPS URL (optional for Ship-initiated sync)
                <input
                  type="url"
                  placeholder="https://vessel.example.com"
                  value={remoteBaseUrl}
                  onChange={event => setRemoteBaseUrl(event.target.value)}
                />
              </label>
              <div className="synchronization-actions">
                <button disabled={busy} onClick={saveSettings}>Save Settings</button>
                <button disabled={busy} onClick={rotateToken}>Generate Vessel Token</button>
              </div>
              {generatedToken &&
                <div className="generated-token">
                  <strong>New vessel token — copy it now:</strong>
                  <code>{generatedToken}</code>
                  <button onClick={() => navigator.clipboard.writeText(generatedToken)}>
                    Copy
                  </button>
                </div>}
            </section>}
        </>}

        <section className="synchronization-log">
          <h3>Synchronization Log</h3>
          <div className="dashboard-table-wrap">
            <table className="dashboard-table synchronization-table">
              <thead>
                <tr>
                  <th>Date</th>
                  <th>Vessel</th>
                  <th>Direction</th>
                  <th>Duration</th>
                  <th>Uploaded</th>
                  <th>Downloaded</th>
                  <th>Result</th>
                  <th>Error message</th>
                </tr>
              </thead>
              <tbody>
                {logs.map(log => (
                  <tr key={log.id}>
                    <td>{new Date(log.dateUtc).toLocaleString("en-GB")}</td>
                    <td>{log.vessel}</td>
                    <td>{log.direction}</td>
                    <td>{log.durationSeconds}s</td>
                    <td>{log.uploadedItems}</td>
                    <td>{log.downloadedItems}</td>
                    <td><span className={`sync-state ${log.result.toLowerCase()}`}>{log.result}</span></td>
                    <td>{log.errorMessage ?? "—"}</td>
                  </tr>
                ))}
              </tbody>
            </table>
          </div>
          {logs.length === 0 &&
            <div className="empty-state">No synchronization attempts recorded.</div>}
        </section>
      </div>
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

function CreateVesselDialog({
  token,
  onClose,
  onCreated,
}: {
  token: string;
  onClose: () => void;
  onCreated: (vessel: Vessel) => Promise<void>;
}) {
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState("");
  const [draft, setDraft] = useState({
    vesselName: "",
    imoNumber: "",
    flag: "",
    companyName: "",
    invoicingDetails: "",
    accountingEmail: "",
  });

  async function create() {
    if (!draft.vesselName.trim()) {
      setError("Vessel Name is required.");
      return;
    }

    if (!/^\d{7}$/.test(draft.imoNumber.trim())) {
      setError("IMO Number must contain 7 digits.");
      return;
    }

    setBusy(true);
    setError("");

    try {
      const response = await authFetch(
        `${apiBaseUrl}/vessels`,
        token,
        {
          method: "POST",
          headers: {
            "Content-Type": "application/json",
          },
          body: JSON.stringify({
            vesselName: draft.vesselName.trim(),
            imoNumber: draft.imoNumber.trim(),
            flag: draft.flag.trim() || null,
            companyName: draft.companyName.trim() || null,
            invoicingDetails:
              draft.invoicingDetails.trim() || null,
            accountingEmail:
              draft.accountingEmail.trim() || null,
          }),
        },
      );

      if (!response.ok) {
        const body = await response.json().catch(() => null);
        throw new Error(
          body?.message ?? "Unable to create vessel.",
        );
      }

      await onCreated(await response.json());
    } catch (exception) {
      setError((exception as Error).message);
    } finally {
      setBusy(false);
    }
  }

  return (
    <div className="modal">
      <div className="vessel-profile-dialog">
        <div className="service-request-header">
          <div>
            <h2>Add Vessel</h2>
            <p>Create a vessel in the Office fleet.</p>
          </div>

          <button type="button" disabled={busy} onClick={onClose}>
            Close
          </button>
        </div>

        {error && <div className="error">{error}</div>}

        <div className="vessel-profile-form">
          <label>
            Vessel Name *
            <input
              autoFocus
              value={draft.vesselName}
              onChange={event =>
                setDraft(current => ({
                  ...current,
                  vesselName: event.target.value,
                }))}
            />
          </label>

          <label>
            IMO Number *
            <input
              maxLength={7}
              value={draft.imoNumber}
              onChange={event =>
                setDraft(current => ({
                  ...current,
                  imoNumber: event.target.value,
                }))}
            />
          </label>

          <label>
            Flag
            <input
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
              value={draft.companyName}
              onChange={event =>
                setDraft(current => ({
                  ...current,
                  companyName: event.target.value,
                }))}
            />
          </label>

          <label className="wide-field">
            Invoicing Details
            <textarea
              rows={5}
              value={draft.invoicingDetails}
              onChange={event =>
                setDraft(current => ({
                  ...current,
                  invoicingDetails: event.target.value,
                }))}
            />
          </label>

          <label className="wide-field">
            Accounting Email
            <input
              type="email"
              value={draft.accountingEmail}
              onChange={event =>
                setDraft(current => ({
                  ...current,
                  accountingEmail: event.target.value,
                }))}
            />
          </label>
        </div>

        <div className="service-request-actions">
          <button type="button" disabled={busy} onClick={onClose}>
            Cancel
          </button>
          <button
            type="button"
            className="primary"
            disabled={busy}
            onClick={create}
          >
            {busy ? "Creating..." : "Create Vessel"}
          </button>
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
