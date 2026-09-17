import fs from 'node:fs';
import vm from 'node:vm';

const sourcePath = new URL('../vendor/ff14-fish-tracker-app/js/app/data.js', import.meta.url);
const infoSourcePath = new URL('../vendor/ff14-fish-tracker-app/js/app/fish_info_data.js', import.meta.url);
const gatherBuddyFishPath = new URL('../vendor/GatherBuddy/GatherBuddy.GameData/Data/Fish/', import.meta.url);
const outputPath = new URL('../src/FishingClues/FishConditions.json', import.meta.url);
const source = fs.readFileSync(sourcePath, 'utf8') + '\n;globalThis.__fishData = DATA;';
const context = {};
vm.createContext(context);
vm.runInContext(source, context);
const data = context.__fishData;

const infoSource = fs.readFileSync(infoSourcePath, 'utf8') + '\n;globalThis.__fishInfo = FISH_INFO;';
const infoContext = {};
vm.createContext(infoContext);
vm.runInContext(infoSource, infoContext);
const fishInfoRows = infoContext.__fishInfo;

const fish = {};
const referencedItems = new Set();
const referencedWeather = new Set();
const referencedFolklore = new Set();

for (const [itemId, row] of Object.entries(data.FISH)) {
  const rawPath = row.bestCatchPath ?? [];
  const alternativeBaits = Array.isArray(rawPath[0]) ? rawPath[0] : [];
  const path = alternativeBaits.length > 0 ? [alternativeBaits[0], ...rawPath.slice(1)] : rawPath;
  const predators = row.predators ?? [];
  for (const id of path) referencedItems.add(String(id));
  for (const [id] of predators) referencedItems.add(String(id));
  for (const id of row.previousWeatherSet ?? []) referencedWeather.add(String(id));
  for (const id of row.weatherSet ?? []) referencedWeather.add(String(id));
  if (row.folklore != null) referencedFolklore.add(String(row.folklore));

  fish[itemId] = {
    previousWeather: row.previousWeatherSet ?? [],
    weather: row.weatherSet ?? [],
    startHour: row.startHour ?? 0,
    endHour: row.endHour ?? 24,
    baitPath: path,
    alternativeBaits,
    predators,
    intuitionSeconds: row.intuitionLength ?? null,
    folklore: row.folklore ?? null,
    fishEyes: row.fishEyes ?? null,
    snagging: row.snagging ?? null,
    lure: row.lure ?? null,
    hookset: row.hookset ?? null,
    tug: row.tug ?? null,
    gig: row.gig ?? null,
    dataMissing: row.dataMissing ?? null
  };
}

// The tracker intentionally omits many ordinary, unrestricted fish from DATA.FISH.
// Fill those gaps from GatherBuddy's catch definitions while keeping tracker rows
// authoritative whenever both sources contain the same fish.
const blankCondition = () => ({
  previousWeather: [], weather: [], startHour: 0, endHour: 24,
  baitPath: [], alternativeBaits: [], predators: [], intuitionSeconds: null, folklore: null,
  fishEyes: null, snagging: null, lure: null, hookset: null, tug: null,
  gig: null, dataMissing: null,
});

for (const fileName of fs.readdirSync(gatherBuddyFishPath).filter(name => /^Data\d.*\.cs$/.test(name))) {
  const source = fs.readFileSync(new URL(fileName, gatherBuddyFishPath), 'utf8');
  for (const match of source.matchAll(/data\.Apply\s*\(\s*(\d+)\s*,[\s\S]*?;/g)) {
    const itemId = match[1];
    if (fish[itemId] != null) continue;
    const block = match[0];
    const row = blankCondition();

    const bait = block.match(/\.Bait\s*\(\s*data\s*,\s*(\d+)\s*\)/);
    const mooch = block.match(/\.Mooch\s*\(\s*data\s*,\s*([^)]+)\)/);
    if (bait) row.baitPath = [Number(bait[1])];
    else if (mooch) row.baitPath = [...mooch[1].matchAll(/\d+/g)].map(value => Number(value[0]));

    const time = block.match(/\.Time\s*\(\s*(\d+)\s*,\s*(\d+)\s*\)/);
    if (time) {
      row.startHour = Number(time[1]) / 60;
      row.endHour = Number(time[2]) / 60;
    }
    const weather = block.match(/\.Weather\s*\(\s*data\s*,\s*([^)]+)\)/);
    if (weather) row.weather = [...weather[1].matchAll(/\d+/g)].map(value => Number(value[0]));
    const transition = block.match(/\.Transition\s*\(\s*data\s*,\s*([^)]+)\)/);
    if (transition) row.previousWeather = [...transition[1].matchAll(/\d+/g)].map(value => Number(value[0]));

    const bite = block.match(/\.Bite\s*\(\s*data\s*,\s*HookSet\.(\w+)(?:\s*,\s*BiteType\.(\w+))?\s*\)/);
    if (bite) {
      row.hookset = bite[1] === 'Precise' ? 'Precision' : bite[1] === 'Powerful' ? 'Powerful' : null;
      row.tug = bite[2] === 'Weak' ? 'light' : bite[2] === 'Strong' ? 'medium' : bite[2] === 'Legendary' ? 'heavy' : null;
    }
    const snag = block.match(/\.Snag\s*\(\s*data\s*,\s*Snagging\.(\w+)\s*\)/);
    if (snag) row.snagging = snag[1] === 'Required';
    const lure = block.match(/\.Lure\s*\(\s*(?:Enums\.)?Lure\.(\w+)\s*\)/);
    if (lure) row.lure = lure[1];
    const predators = block.match(/\.Predators\s*\(\s*data\s*,\s*(\d+)\s*,\s*([\s\S]*?)\)/);
    if (predators) {
      row.intuitionSeconds = Number(predators[1]);
      row.predators = [...predators[2].matchAll(/\(\s*(\d+)\s*,\s*(\d+)\s*\)/g)]
        .map(value => [Number(value[1]), Number(value[2])]);
    }
    const spear = block.match(/\.Spear\s*\(\s*data\s*,\s*SpearfishSize\.(\w+)/);
    if (spear) row.gig = spear[1];

    for (const id of row.baitPath) referencedItems.add(String(id));
    for (const [id] of row.predators) referencedItems.add(String(id));
    for (const id of row.previousWeather) referencedWeather.add(String(id));
    for (const id of row.weather) referencedWeather.add(String(id));
    fish[itemId] = row;
  }
}

const items = Object.fromEntries([...referencedItems].sort((a, b) => Number(a) - Number(b)).map(id => [id, data.ITEMS[id]?.name_en ?? `Item ${id}`]));
const weather = Object.fromEntries([...referencedWeather].sort((a, b) => Number(a) - Number(b)).map(id => [id, data.WEATHER_TYPES[id]?.name_en ?? `Weather ${id}`]));
const folklore = Object.fromEntries([...referencedFolklore].sort((a, b) => Number(a) - Number(b)).map(id => [id, data.FOLKLORE[id]?.book_en ?? data.FOLKLORE[id]?.name_en ?? `Folklore ${id}`]));
const info = Object.fromEntries(fishInfoRows.map(row => [String(row.id), {
  name: row.name_en ?? '',
  description: row.desc_en ?? '',
  icon: Number(row.icon ?? 0),
  level: Array.isArray(row.level) ? (row.level[0] ?? 0) : 0,
  stars: Array.isArray(row.level) ? (row.level[1] ?? 0) : 0,
  waters: row.record_en ?? '',
  region: row.region_en ?? '',
  zone: row.zone_en ?? '',
  collectable: row.collectable === true,
  rarity: row.rarity ?? 0,
}]));

fs.mkdirSync(new URL('../src/FishingClues/', import.meta.url), { recursive: true });
fs.writeFileSync(outputPath, JSON.stringify({ schemaVersion: 1, fish, items, weather, folklore, info }));
