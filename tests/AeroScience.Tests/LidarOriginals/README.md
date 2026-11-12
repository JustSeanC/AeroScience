# Genuine historical LiDAR fixture

`ia-fullstate-root.laz` is the unchanged 603,312-byte public EPT root tile from
https://usgs-lidar-public.s3-us-west-2.amazonaws.com/IA_FullState/ept-data/0-0-0-0.laz .
`ia-fullstate-ept.json` is the separately retained delivered project metadata.
Retrieved for explicit P05c acceptance on 2026-11-12; US Government Public Domain,
https://registry.opendata.aws/usgs-lidar/ . No Requester Pays data was accessed.

Original SHA-256: `68c4b0ed247501a15d020d9a758d0345904f38785235732bd8e3386a3fca71c1`.
34,923 authentic historical delivered point records, EPSG:3857, LAS 1.2 format 1,
with intensity/returns/classification/flags/GPS bits and extra `OriginId` bytes.
This single octree level is a sparse subset, not full-density Iowa coverage.
Survey dates, vertical datum and height unit are unknown in this EPT metadata.
It does not establish the original contractor's coordinate values before Entwine.

`independent-reference.json` records comparison of every record against laspy
2.6.1 + lazrs 0.7.0 (independent Rust decoder), including the canonical native
protocol hash. GPS original bit patterns are preserved even where values are
nonfinite or their time reference is unknown. JSON summaries never invent dates.
All generated flight paths and format edge fixtures in tests are synthetic.
