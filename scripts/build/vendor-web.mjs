// Maintainer-only regeneration after npm ci. Locked package hashes come from package-lock.json.
import { cpSync, mkdirSync } from "node:fs";
const target = "src/AeroScience.Engine/wwwroot/vendor";
mkdirSync(target, { recursive: true });
for (const [source, dest] of [
  ["leaflet/dist/leaflet.js", "leaflet/leaflet.js"],
  ["leaflet/dist/leaflet.css", "leaflet/leaflet.css"],
  ["leaflet/dist/images", "leaflet/images"],
  ["leaflet/LICENSE", "leaflet/LICENSE.txt"],
  ["leaflet-draw/dist/leaflet.draw.js", "leaflet-draw/leaflet.draw.js"],
  ["leaflet-draw/dist/leaflet.draw.css", "leaflet-draw/leaflet.draw.css"],
  ["leaflet-draw/dist/images", "leaflet-draw/images"],
  ["chart.js/dist/chart.umd.js", "chart.js/chart.umd.min.js"],
  ["chart.js/LICENSE.md", "chart.js/LICENSE.txt"],
])
  cpSync("node_modules/" + source, target + "/" + dest, { recursive: true });
