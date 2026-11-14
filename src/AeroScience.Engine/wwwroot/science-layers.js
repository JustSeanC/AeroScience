import {message as localizedCopy} from './i18n.js';
import {t as textForUI} from './i18n.js';
export const measurements = [['elevation','DEM-derived terrain elevation'],['temperature',localizedCopy("ui.scienceLayers.messageHistoricalSurfaceTemperatureK")],['temperature-celsius',localizedCopy("ui.scienceLayers.messageHistoricalSurfaceTemperatureC")],
  ['natural-color',localizedCopy("ui.scienceLayers.messageNaturalColorReflectance")],['false-color',localizedCopy("ui.scienceLayers.messageFalseColorNIRRed")],['ndvi',localizedCopy("ui.scienceLayers.messageVegetationIndexNDVI")],['B04',localizedCopy("ui.scienceLayers.messageRedReflectance")],['B08',localizedCopy("ui.scienceLayers.messageNearInfraredReflectance")]];
export const initialMeasurement = (instrument, source) => instrument === 'thermal-imager' ? 'temperature-celsius'
  : instrument === 'multispectral-imager' ? 'ndvi' : instrument === 'hyperspectral-imager'
    ? source?.manifest.bands.find(b => !b.badBand)?.id : instrument === 'terrain-scanner' ? 'elevation' : undefined;
export const layerSignature = layer => JSON.stringify([layer.unit, layer.source.kind, layer.source.verticalDatum, layer.wavelengthNm ?? null]);
export const measurementsFor = (instrument, source) => instrument === 'terrain-scanner' ? measurements.filter(([id]) => id === 'elevation')
  : instrument === 'thermal-imager' ? measurements.filter(([id]) => id.startsWith('temperature'))
  : instrument === 'multispectral-imager' ? measurements.filter(([id]) => ['natural-color','false-color','ndvi','B04','B08'].includes(id))
  : instrument === 'hyperspectral-imager' ? (source?.manifest.bands || []).filter(b => !b.badBand).map(b => [b.id, textForUI("ui.scienceLayers.messageNm",{value1:(String(b.id)),value2:(String(b.wavelengthNm ?? 'unknown'))})]) : [];
