export function areaSummary(points) {
  if(points.length<3)return {error:"Click at least three different points to form a survey area."};
  if(points.some(p=>!Number.isFinite(p.latitude)||!Number.isFinite(p.longitude)||Math.abs(p.latitude)>90||Math.abs(p.longitude)>180))return {error:"Use valid geographic coordinates."};
  const base=points[0].longitude;
  const wrap=x=>((x+540)%360)-180;
  const xy=points.map(p=>[base+wrap(p.longitude-base),p.latitude]);
  if(new Set(xy.map(p=>p.join(","))).size!==points.length)return {error:"Survey vertices must be distinct; Finish Area closes the polygon for you."};
  const cross=(a,b,c)=>(b[0]-a[0])*(c[1]-a[1])-(b[1]-a[1])*(c[0]-a[0]);
  for(let i=0;i<xy.length;i++)for(let j=i+1;j<xy.length;j++) {
    if(j===i+1||i===0&&j===xy.length-1)continue;
    const a=xy[i],b=xy[(i+1)%xy.length],c=xy[j],d=xy[(j+1)%xy.length];
    if(cross(a,b,c)*cross(a,b,d)<=0&&cross(c,d,a)*cross(c,d,b)<=0&&Math.max(Math.min(a[0],b[0]),Math.min(c[0],d[0]))<=Math.min(Math.max(a[0],b[0]),Math.max(c[0],d[0]))&&Math.max(Math.min(a[1],b[1]),Math.min(c[1],d[1]))<=Math.min(Math.max(a[1],b[1]),Math.max(c[1],d[1])))return {error:"The polygon crosses or touches itself. Undo a vertex or draw a simpler boundary."};
  }
  const rad=Math.PI/180,R=6371008.8;
  let sum=0;for(let i=0;i<xy.length;i++){const a=xy[i],b=xy[(i+1)%xy.length];sum+=(b[0]-a[0])*rad*(2+Math.sin(a[1]*rad)+Math.sin(b[1]*rad));}
  const squareMetres=Math.abs(sum*R*R/2);if(squareMetres<1)return {error:"The polygon has no usable area. Place points around a region instead of on one line."};
  const latitude=xy.reduce((s,p)=>s+p[1],0)/xy.length;const longitude=wrap(xy.reduce((s,p)=>s+p[0],0)/xy.length);
  const width=(Math.max(...xy.map(p=>p[0]))-Math.min(...xy.map(p=>p[0])))*rad*R*Math.cos(latitude*rad);
  const length=(Math.max(...xy.map(p=>p[1]))-Math.min(...xy.map(p=>p[1])))*rad*R;
  return {squareMetres,widthMetres:width,lengthMetres:length,center:{latitude,longitude},description:`Approximate area ${(squareMetres/1e6).toFixed(2)} km² · E–W ${(width/1000).toFixed(2)} km × N–S ${(length/1000).toFixed(2)} km (spherical geographic estimate)`};
}
