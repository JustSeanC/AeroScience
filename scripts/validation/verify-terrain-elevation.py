#!/usr/bin/env python3
"""Independent GDAL/OGR oracle for the bounded P05b genuine-source probe.

Run with a Python environment containing GDAL/OGR (not AeroScience's readers).
This validates delivered geographic raster cells and clipping, not the unknown
native survey date/datum, provider-wide acceptance or installed MSFS behavior.
"""
import argparse
import datetime
import hashlib
import json
import math
import pathlib
import struct

from osgeo import gdal, ogr, osr


def verify(folder):
    gdal.UseExceptions()
    # Probe metadata uses the existing preference serializer (PascalCase);
    # scientific artifacts use WireJson (camelCase). Normalize field names only.
    def fields(value):
        if isinstance(value, dict):
            return {key[:1].lower() + key[1:]: fields(item) for key, item in value.items()}
        if isinstance(value, list):
            return [fields(item) for item in value]
        return value

    def read(name):
        return fields(json.loads((folder / name).read_text()))

    probe, native, source, index = [read(n) for n in ("probe.json", "native-grid.json", "source.json", "index.json")]
    original = folder / probe["originalSource"]
    dataset = gdal.Open(str(original), gdal.GA_ReadOnly)
    assert dataset is not None and dataset.RasterXSize * dataset.RasterYSize <= 4096
    spatial = osr.SpatialReference(wkt=dataset.GetProjectionRef())
    assert spatial.GetAuthorityCode(None) == "4326", "This independent probe supports delivered WGS84 grids only."
    gt = dataset.GetGeoTransform()
    grid = native["grid"]
    assert [grid["width"], grid["height"]] == [dataset.RasterXSize, dataset.RasterYSize]
    assert all(abs(a - b) < 1e-12 for a, b in zip(gt, [grid[k] for k in ("originX", "pixelWidth", "rowX", "originY", "columnY", "pixelHeight")]))
    band = dataset.GetRasterBand(1)
    values = struct.unpack("=" + "f" * (grid["width"] * grid["height"]), band.ReadRaster(buf_type=gdal.GDT_Float32))
    nodata = band.GetNoDataValue()
    valid = lambda v: math.isfinite(v) and (nodata is None or v != nodata)
    assert len(native["cells"]) == len(values)
    for cell in native["cells"]:
        value = values[cell["row"] * grid["width"] + cell["column"]]
        assert cell["raw"] == (value if valid(value) else None)
        assert cell["value"] == (value if valid(value) else None)
        x, y = gdal.ApplyGeoTransform(gt, cell["column"] + .5, cell["row"] + .5)
        assert abs(cell["point"]["longitude"] - x) < 1e-12 and abs(cell["point"]["latitude"] - y) < 1e-12
    inverse = gdal.InvGeoTransform(gt)

    def geometry(record, transform=False):
        coordinates = record["coordinates"]
        if transform:
            coordinates = [[[list(gdal.ApplyGeoTransform(inverse, *p)) for p in ring] for ring in poly] for poly in coordinates]
        return ogr.CreateGeometryFromJson(json.dumps({"type": "MultiPolygon", "coordinates": coordinates}))

    mask = None
    for record in read("geometry.json"):
        if record.get("chunk"):
            shape = geometry(record["chunk"]["geometry"], True)
            mask = shape if mask is None else mask.Union(shape)
    assert mask is not None and mask.IsValid()
    expected = {}
    for row in range(grid["height"]):
        for col in range(grid["width"]):
            ring = ogr.Geometry(ogr.wkbLinearRing)
            for p in ((col, row), (col + 1, row), (col + 1, row + 1), (col, row + 1), (col, row)):
                ring.AddPoint_2D(*p)
            box = ogr.Geometry(ogr.wkbPolygon)
            box.AddGeometry(ring)
            clipped = mask.Intersection(box)
            if clipped.GetArea() > 0:
                expected[(col, row)] = clipped
    actual = {}
    for path in sorted((folder / "tiles").glob("*.json")):
        tile = json.loads(path.read_text())
        assert tile["source"]["surface"] == "composite-dem" and not tile["source"]["datumVerified"]
        assert tile["source"]["original"]["manifest"]["observationUtc"] is None
        assert not tile["grantsOriginalScienceCredit"]
        for cell in tile["cells"]:
            key = (cell["column"], cell["row"])
            assert key not in actual, "Repeated passes must not duplicate native cells."
            actual[key] = cell
    assert actual.keys() == expected.keys() and len(actual) > 0
    max_fraction_error = 0
    for (col, row), cell in actual.items():
        reference = expected[(col, row)]
        error = abs(cell["nativeCellSupportFraction"] - reference.GetArea())
        max_fraction_error = max(max_fraction_error, error)
        assert error < 1e-7
        support = None
        for item in cell["support"]:
            shape = geometry(item["mask"])
            support = shape if support is None else support.Union(shape)
        assert support.SymmetricDifference(reference).GetArea() < 1e-7
        value = values[row * grid["width"] + col]
        assert cell["value"] == (value if valid(value) else None)
        assert cell["quality"] == ("valid" if valid(value) else "nodata")
    assert not index["grantsOriginalScienceCredit"] and probe["originalScienceObservations"] == probe["originalScienceProducts"] == 0
    result = {
        "state": "Passed", "verifiedUtc": datetime.datetime.now(datetime.timezone.utc).isoformat(),
        "scope": "Genuine bounded USGS composite delivered values/grid; synthetic archived flight corridor; fully offline application processing",
        "reference": "GDAL " + gdal.VersionInfo("RELEASE_NAME") + " / OGR GEOS overlays", "originalSha256": hashlib.sha256(original.read_bytes()).hexdigest(),
        "sourceManifestSha256": source["manifestSha256"], "algorithm": probe["algorithm"], "nativeCellsCompared": len(values),
        "clippedCellsCompared": len(actual), "partialCells": sum(0 < c["nativeCellSupportFraction"] < .999999 for c in actual.values()),
        "maximumCellFractionDifference": max_fraction_error, "derivedIndexSha256": probe["sha256"],
        "unknowns": ["historical survey date", "native per-pixel resolution", "vertical datum / transformations"],
        "notTested": ["genuine Copernicus DSM", "genuine UTM provider source", "installed Windows/MSFS", "provider-wide availability"]
    }
    receipt = folder / "independent-comparison.json"
    if receipt.exists():
        raise FileExistsError("Preserve the earlier comparison receipt; choose a new probe output directory.")
    receipt.write_text(json.dumps(result, indent=2) + "\n")
    print(json.dumps(result))


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("probe_directory", type=pathlib.Path)
    verify(parser.parse_args().probe_directory.resolve())
