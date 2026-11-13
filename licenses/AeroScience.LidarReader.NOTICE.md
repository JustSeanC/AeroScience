## P05c pinned LiDAR decoder

LASzip source at tag 3.5.0, commit `fa089bd8b2b4ca5631e199d257374c32a125f73f`,
is vendored unmodified under `native/lidar/laszip` under Apache-2.0.
See `licenses/LASzip.txt`, the retained upstream authors/copyright notices and
`native/lidar/source-policy.json`. AeroScience's separate read-only worker is
built offline from those verified sources; no decoder executable is downloaded
or installed at runtime. Upstream's tag includes API 3.5.1 constants; the exact
source commit, not that version text, determines the decoder identity.

LASzip authors include Martin Isenburg, Howard Butler and Michael P. Gerlek.
Upstream component copyrights, including (c) 2007-2022 rapidlasso GmbH, remain
in the unmodified source. The complete Apache-2.0 license is bundled beside
this notice as LASzip.txt. Source: https://github.com/LASzip/LASzip/tree/fa089bd8b2b4ca5631e199d257374c32a125f73f
