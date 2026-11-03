// One bounded canvas layer per acquisition, using exact saved native-cell polygons.
// No interpolation, smoothing, or footprint filling; missing cells stay transparent.
export function terrainSurface(L,features,{color,onInspect,details=false,opacity=.8}={}){
 const Surface=L.Layer.extend({
  initialize(){this.features=features;this.terrainCount=features.length;this.options={opacity};this.details=details;this.scale=null;},
  onAdd(map){this.map=map;this.canvas=L.DomUtil.create('canvas','terrain-elevation-surface leaflet-zoom-hide');this.canvas.style.pointerEvents='none';map.getPane('overlayPane').appendChild(this.canvas);map.on('moveend zoomend resize',this.redraw,this);map.on('click',this.inspect,this);this.redraw();},
  onRemove(map){map.off('moveend zoomend resize',this.redraw,this);map.off('click',this.inspect,this);this.canvas.remove();this.map=null;},
  setStyle(style){this.options.opacity=style.fillOpacity??style.opacity??this.options.opacity;this.redraw();return this;},
  setScale(low,high){this.scale={low,high};this.redraw();},
  setDetails(value){this.details=value;this.redraw();},
  redraw(){if(!this.map)return;const size=this.map.getSize(),origin=this.map.containerPointToLayerPoint([0,0]);L.DomUtil.setPosition(this.canvas,origin);this.canvas.width=Math.min(4096,size.x);this.canvas.height=Math.min(4096,size.y);const c=this.canvas.getContext('2d');c.globalAlpha=this.options.opacity;
   for(const feature of this.features){const ring=feature.geometry.coordinates[0],points=ring.map(([lon,lat])=>this.map.latLngToContainerPoint([lat,lon]));if(points.every(p=>p.x<0)||points.every(p=>p.y<0)||points.every(p=>p.x>size.x)||points.every(p=>p.y>size.y))continue;c.beginPath();points.forEach((p,i)=>i?c.lineTo(p.x,p.y):c.moveTo(p.x,p.y));c.closePath();c.fillStyle=color(feature.properties.value,this.scale?.low,this.scale?.high);c.fill();if(this.details){c.strokeStyle='#233440';c.lineWidth=.65;c.stroke();}}
  },
  inspect(event){if(this.map.terrainProfileDrawing)return;const x=event.latlng.lng,y=event.latlng.lat;
   for(let index=this.features.length-1;index>=0;index--){const ring=this.features[index].geometry.coordinates[0];let inside=false;for(let i=0,j=ring.length-1;i<ring.length;j=i++){const a=ring[i],b=ring[j];if((a[1]>y)!==(b[1]>y)&&x<(b[0]-a[0])*(y-a[1])/(b[1]-a[1])+a[0])inside=!inside;}if(inside){onInspect?.(index);return;}}
  }
 });return new Surface();
}
