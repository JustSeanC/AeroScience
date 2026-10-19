"""Independent developer GIS check of genuine retained-source workspace outputs.

Requires rasterio/numpy in a development environment, never in the shipped app.
Reads unmodified retained originals and actual saved acquisitions; no mocked provider replies.
"""
import argparse
from datetime import datetime, timezone
import hashlib
import json
from contextlib import ExitStack
from pathlib import Path
import numpy as np
import rasterio
from rasterio import Affine
from rasterio.warp import transform


def digest(path):
    with open(path, "rb") as stream:
        return hashlib.file_digest(stream, "sha256").hexdigest()


def verify(folder):
    folder = Path(folder)
    report = json.loads((folder / "workspace-acceptance.json").read_text())
    if report["evidenceType"] != "genuine-external-data-and-synthetic-flight-input" or report["windowsMsfs"] != "not tested":
        raise ValueError("Expected genuine source / explicitly synthetic flight evidence")
    library = folder / "ScienceLibrary"
    results = []
    catalog = {s["id"]: s for s in report.get("retainedSources", [v["source"] for v in report["results"]])}
    for item in report["results"]:
        source = item["source"]
        ids = {sid for a in item["artifacts"] for sid in json.loads(a["metadataJson"]).get("sourceIds", [source["id"]])}
        with ExitStack() as stack:
            contexts = {}
            for sid in ids:
                retained = catalog[sid]
                source_root = library / retained["relativeDirectory"]
                for original in retained["files"]:
                    path = source_root / original["relativePath"]
                    if path.stat().st_size != original["bytes"] or digest(path) != original["sha256"]:
                        raise ValueError("Original source integrity failure: " + str(path))
                definitions = {b["id"]: b for b in retained["manifest"]["bands"]}
                readers = {b["id"]: stack.enter_context(rasterio.open(source_root / b["file"])) for b in retained["manifest"]["bands"] if not b["badBand"]}
                contexts[sid] = (retained, readers, definitions)
            def raw_values(points, crs, source_id):
                active, readers, definitions = contexts[source_id]
                output = {}
                for name, dataset in readers.items():
                    band = definitions[name]
                    x, y = transform(crs, dataset.crs, points[:, 0].tolist(), points[:, 1].tolist())
                    sampled = np.array([v[0] for v in dataset.sample(zip(x, y), indexes=band["channel"] + 1, masked=True)], dtype=np.float64)
                    for nodata in (dataset.nodata, band.get("noData")):
                        if nodata is not None:
                            sampled[sampled == nodata] = np.nan
                    # Read coordinates outside the original raster as nodata, independently of a TIFF default fill.
                    ij = [dataset.index(a, b) for a, b in zip(x, y)]
                    sampled[[r < 0 or c < 0 or r >= dataset.height or c >= dataset.width for r, c in ij]] = np.nan
                    output[name] = sampled * band["scale"] + band["offset"]
                kind = active["manifest"]["kind"]
                if kind == "thermal":
                    qa = output["QA_PIXEL"]
                    keep = np.isfinite(qa) & (qa == np.floor(qa)) & (qa >= 0) & (qa <= 65535)
                    keep &= (np.nan_to_num(qa).astype(np.int64) & 63) == 0
                    keep &= (output["ST_B10"] >= 100) & (output["ST_B10"] <= 500)
                    if "QA_RADSAT" in output:
                        keep &= output["QA_RADSAT"] == 0
                    output["temperature"] = np.where(keep, output["ST_B10"], np.nan)
                    output["temperature-celsius"] = output["temperature"] - 273.15
                elif kind == "multispectral":
                    scl = output["SCL"]
                    keep = np.isfinite(scl) & np.isin(scl, [2, 4, 5, 6, 7])
                    for name in output:
                        if name != "SCL":
                            output[name] = np.where(keep, output[name], np.nan)
                    den = output["B08"] + output["B04"]
                    with np.errstate(divide="ignore", invalid="ignore"):
                        ndvi = (output["B08"] - output["B04"]) / den
                    output["ndvi"] = np.where((np.abs(den) > 1e-12) & (ndvi >= -1) & (ndvi <= 1), ndvi, np.nan)
                elif kind == "terrain":
                    output["elevation"] = output[next(iter(definitions))].copy()
                return output

            for artifact in item["artifacts"]:
                metadata = json.loads(artifact["metadataJson"])
                path = library / artifact["relativePath"]
                if digest(path) != artifact["sha256"]:
                    raise ValueError("Derived output hash mismatch")
                with rasterio.open(path) as raster:
                    grid = metadata["grid"]
                    affine = Affine(grid["pixelWidth"], grid["rowX"], grid["originX"], grid["columnY"], grid["pixelHeight"], grid["originY"])
                    if raster.transform != affine or str(raster.crs) != grid["crs"] or raster.width != grid["width"] or raster.height != grid["height"]:
                        raise ValueError("GeoTIFF native affine/CRS/dimensions mismatch")
                    if raster.count != len(metadata["bands"]) or not np.isnan(raster.nodata):
                        raise ValueError("GeoTIFF channels/nodata mismatch")
                    rows, cols = np.indices((raster.height, raster.width))
                    xs, ys = rasterio.transform.xy(raster.transform, rows.ravel(), cols.ravel())
                    expected = np.full((raster.count, raster.height, raster.width), np.nan)
                    for sid in metadata.get("sourceIds", [source["id"]]):
                        reference = raw_values(np.column_stack([xs, ys]), raster.crs, sid)
                        candidate = np.stack([reference[name].reshape(raster.height, raster.width) for name in metadata["bands"]])
                        usable = np.all(np.isfinite(candidate), axis=0)
                        if metadata["selection"] == "first":
                            usable &= ~np.all(np.isfinite(expected), axis=0)
                        expected[:, usable] = candidate[:, usable]
                    if metadata["mode"] == "mission-samples":
                        expected[:] = np.nan
                        inputs = []
                        for acquisition in metadata["acquisitionIds"]:
                            files = list((library / "Products" / metadata["missionId"].replace("-", "")).glob(acquisition.replace("-", "") + ".json"))
                            if len(files) != 1:
                                raise ValueError("Saved acquisition referenced by composite is missing")
                            inputs.append(json.loads(files[0].read_text()))
                        inputs.sort(key=lambda d: (d["product"]["flightPacketUtc"], d["product"]["id"]))
                        occupied = set()
                        for data in inputs:
                            points = data["sampleLocations"]
                            original = raw_values(np.array([[p["longitude"], p["latitude"]] for p in points]), "EPSG:4326", data["product"]["sourceId"])
                            px, py = transform("EPSG:4326", raster.crs, [p["longitude"] for p in points], [p["latitude"] for p in points])
                            for i, (x, y) in enumerate(zip(px, py)):
                                row, col = raster.index(x, y)
                                if row < 0 or col < 0 or row >= raster.height or col >= raster.width:
                                    continue
                                values = np.array([original[b][i] for b in metadata["bands"]])
                                if not np.all(np.isfinite(values)) or metadata["selection"] == "first" and (row, col) in occupied:
                                    continue
                                expected[:, row, col] = values
                                occupied.add((row, col))
                    actual = raster.read()
                    if not np.array_equal(np.isnan(actual), np.isnan(expected)):
                        raise ValueError("Scientific nodata/support mismatch: " + str(path))
                    valid = np.isfinite(actual)
                    maximum = float(np.max(np.abs(actual[valid] - expected[valid]))) if np.any(valid) else 0
                    if not np.allclose(actual[valid], expected[valid], rtol=1e-12, atol=1e-9):
                        raise ValueError("Numeric values differ from independently read originals: " + str(path))
                    results.append({"sourceId": source["id"], "sourceScene": source["manifest"]["sceneId"], "artifactId": artifact["id"], "mode": metadata["mode"], "bands": metadata["bands"], "pixelsChecked": int(actual.size), "validValues": int(np.count_nonzero(valid)), "maximumAbsoluteDifference": maximum, "nodataSupportMatches": True, "nativeAffineAndCrsMatch": True, "sha256": artifact["sha256"]})
    if not results:
        raise ValueError("No actual scientific raster was checked")
    return {"formatVersion": 1, "evidenceType": "independent-genuine-raster-comparison", "checkedUtc": datetime.now(timezone.utc).isoformat(), "tool": f"rasterio {rasterio.__version__} / GDAL {rasterio.__gdal_version__} / numpy {np.__version__}", "windowsMsfs": "not tested", "flightInput": "synthetic test positions; genuine external originals", "results": results}


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--folder", type=Path, required=True)
    parser.add_argument("--output", type=Path, required=True)
    args = parser.parse_args()
    result = verify(args.folder)
    args.output.write_text(json.dumps(result, indent=2) + "\n")
    print(f"Independently verified {len(result['results'])} genuine native-grid scientific rasters; Windows/MSFS not tested.")
