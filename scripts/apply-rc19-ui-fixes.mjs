import fs from "node:fs";

const file = "frontend/src/main.tsx";
let source = fs.readFileSync(file, "utf8");

function ensureReplace(pattern, replacement, alreadyPresent, label) {
  if (alreadyPresent.test(source)) {
    console.log(`${label}: already applied.`);
    return;
  }

  if (!pattern.test(source)) {
    throw new Error(`Unable to apply ${label}: expected source structure was not found.`);
  }

  source = source.replace(pattern, replacement);
  console.log(`${label}: applied.`);
}

ensureReplace(
  /(role:\s*"OfficeAdmin"\s*\|\s*"Superintendent"\s*\|\s*"Crew";\s*)(\r?\n\s*};)/,
  '$1\n  edition: "Office" | "Ship";$2',
  /edition:\s*"Office"\s*\|\s*"Ship";/,
  "login edition type",
);

ensureReplace(
  /(setVessels\(vesselItems\);\s*\r?\n\s*setDashboard\(dashboardData\);)/,
  '$1\n\n        // A Ship installation is assigned to one vessel. Open it directly\n        // instead of showing the office fleet dashboard.\n        if (user.edition === "Ship" && vesselItems.length === 1) {\n          setCurrentVessel(vesselItems[0]);\n        }',
  /user\.edition\s*===\s*"Ship"\s*&&\s*vesselItems\.length\s*===\s*1/,
  "Ship vessel auto-open",
);

ensureReplace(
  /<div className="application">/,
  '<div className={`application ${user.edition === "Ship" ? "ship-edition" : "office-edition"}`}>',
  /className=\{`application \$\{user\.edition === "Ship" \? "ship-edition" : "office-edition"\}`\}/,
  "edition CSS class",
);

ensureReplace(
  /\{currentVessel\s*&&\s*\r?\n\s*<button/,
  '{currentVessel && user.edition !== "Ship" &&\n            <button',
  /currentVessel\s*&&\s*user\.edition\s*!==\s*"Ship"/,
  "Ship dashboard button visibility",
);

fs.writeFileSync(file, source, "utf8");
console.log("RC19 Ship UI fixes are present.");
