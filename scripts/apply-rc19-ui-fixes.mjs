import fs from "node:fs";

const file = "frontend/src/main.tsx";
let source = fs.readFileSync(file, "utf8");

const replaceOnce = (from, to, label) => {
  if (!source.includes(from)) {
    throw new Error(`Unable to apply ${label}: expected source text was not found.`);
  }
  source = source.replace(from, to);
};

replaceOnce(
  '  role: "OfficeAdmin" | "Superintendent" | "Crew";\n};',
  '  role: "OfficeAdmin" | "Superintendent" | "Crew";\n  edition: "Office" | "Ship";\n};',
  "login edition type",
);

replaceOnce(
  '        setVessels(vesselItems);\n        setDashboard(dashboardData);',
  '        setVessels(vesselItems);\n        setDashboard(dashboardData);\n\n        // A Ship installation is assigned to one vessel. Open it directly\n        // instead of showing the office fleet dashboard.\n        if (user.edition === "Ship" && vesselItems.length === 1) {\n          setCurrentVessel(vesselItems[0]);\n        }',
  "Ship vessel auto-open",
);

replaceOnce(
  '<div className="application">',
  '<div className={`application ${user.edition === "Ship" ? "ship-edition" : "office-edition"}`}>',
  "edition CSS class",
);

replaceOnce(
  '{currentVessel &&\n            <button',
  '{currentVessel && user.edition !== "Ship" &&\n            <button',
  "Ship dashboard button visibility",
);

fs.writeFileSync(file, source, "utf8");
console.log("RC19 Ship UI fixes applied.");
