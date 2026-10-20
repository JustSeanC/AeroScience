export const measurements = [['elevation','Terrain elevation'],['temperature','Thermal temperature (K)'],['temperature-celsius','Thermal temperature (°C)'],
  ['natural-color','Natural color (reflectance)'],['false-color','False color — NIR / red / green'],['ndvi','Vegetation index (NDVI)'],['B04','Red reflectance'],['B08','Near-infrared reflectance']];
export const initialMeasurement = (instrument, source) => instrument === 'thermal-imager' ? 'temperature-celsius'
  : instrument === 'multispectral-imager' ? 'ndvi' : instrument === 'hyperspectral-imager'
    ? source?.manifest.bands.find(b => !b.badBand)?.id : instrument === 'terrain-scanner' ? 'elevation' : undefined;
export const layerSignature = layer => JSON.stringify([layer.unit, layer.source.kind, layer.source.verticalDatum, layer.wavelengthNm ?? null]);
export const measurementsFor = (instrument, source) => instrument === 'terrain-scanner' ? measurements.filter(([id]) => id === 'elevation')
  : instrument === 'thermal-imager' ? measurements.filter(([id]) => id.startsWith('temperature'))
  : instrument === 'multispectral-imager' ? measurements.filter(([id]) => ['natural-color','false-color','ndvi','B04','B08'].includes(id))
  : instrument === 'hyperspectral-imager' ? (source?.manifest.bands || []).filter(b => !b.badBand).map(b => [b.id, `${b.id} · ${b.wavelengthNm ?? 'unknown'} nm`]) : [];
