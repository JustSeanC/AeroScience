const normalize = value => String(value || "").normalize("NFD").replace(/[\u0300-\u036f]/g, "").toLowerCase().trim();
export function airportLocations(index) {
  return index.airports.map(r => ({kind:"airport", id:r[0], icao:r[1], iata:r[2], name:r[3], city:r[4], region:r[5], country:r[6], latitude:r[7], longitude:r[8], type:r[9], search:normalize(r.slice(0,7).join(" "))}));
}
export function searchLocations(query, airports, places) {
  const q=normalize(query); if(!q) return [];
  const parts=q.split(",");
  if(parts.length===2&&parts.every(s=>s.trim()!==""&&Number.isFinite(Number(s)))&&Math.abs(Number(parts[0]))<=90&&Math.abs(Number(parts[1]))<=180)
    return [{kind:"coordinates",name:`Coordinates ${parts[0].trim()}, ${parts[1].trim()}`,latitude:Number(parts[0]),longitude:Number(parts[1])}];
  const scored=[];
  for(const p of airports) {
    const codes=[p.icao,p.iata,p.id].map(normalize);
    const score=codes.includes(q)?0:codes.some(c=>c&&c.startsWith(q))?1:normalize(p.name).startsWith(q)?2:normalize(p.city)===q?3:p.search.includes(q)?4:99;
    if(score<99)scored.push({p,score});
  }
  for(const f of places) {const p=f.properties;if(normalize([p.NAME,p.name,p.ADM0NAME,p.SOV0NAME].join(" ")).includes(q))scored.push({score:5,p:{kind:"place",name:p.NAME||p.name,city:p.ADM0NAME||p.SOV0NAME,latitude:f.geometry.coordinates[1],longitude:f.geometry.coordinates[0]}});}
  return scored.sort((a,b)=>a.score-b.score||a.p.name.localeCompare(b.p.name)).slice(0,30).map(r=>r.p);
}
export function locationLabel(p) {
  return `${p.name}${p.icao||p.iata ? ` (${[p.icao,p.iata].filter(Boolean).join(" / ")})` : ""}${[p.city,p.region,p.country].filter(Boolean).length ? " · "+[p.city,p.region,p.country].filter(Boolean).join(", ") : ""}`;
}
