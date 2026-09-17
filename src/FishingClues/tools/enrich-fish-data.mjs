import fs from "node:fs";
import path from "node:path";
import { fileURLToPath } from "node:url";

const here = path.dirname(fileURLToPath(import.meta.url));
const project = path.resolve(here, "..");
const tracker = path.resolve(project, "..", "..", "external", "ff14-fish-tracker-app");
const sourcePath = path.join(tracker, "js", "app", "fish_info_data.js");
const destinationPath = path.join(project, "FishConditions.json");

const source = fs.readFileSync(sourcePath, "utf8");
const sourceRows = JSON.parse(source.slice(source.indexOf("["), source.lastIndexOf("];" ) + 1));
const destination = JSON.parse(fs.readFileSync(destinationPath, "utf8"));

destination.info = Object.fromEntries(sourceRows.map(row => [row.id, {
  name: row.name_en ?? "",
  description: row.desc_en ?? "",
  icon: Number.parseInt(row.icon ?? "0", 10) || 0,
  level: Array.isArray(row.level) ? row.level[0] ?? 0 : 0,
  stars: Array.isArray(row.level) ? row.level[1] ?? 0 : 0,
  waters: row.record_en ?? "",
  region: row.region_en ?? "",
  zone: row.zone_en ?? "",
  collectable: row.collectable === true,
  rarity: row.rarity ?? 0,
}]));

fs.writeFileSync(destinationPath, JSON.stringify(destination));
console.log(`Added tracker metadata for ${sourceRows.length} fish.`);
