## Bounded HDF reader foundation added in v0.6.0

- PureHDF 2.2.0 source at `c2cea464ad2a6649258ab14c8d798cb6239b9e1e`, MIT,
  source-pinned under `src/AeroScience.HdfReader`. The read policy rejects external/symbolic links,
  external raw/virtual storage, unsupported filter pipelines and oversized decompression.
  Original notice: [licenses/PureHDF.txt](PureHDF.txt). Exact original/prepared hashes
  and modifications: `src/AeroScience.HdfReader/source-policy.json`.
- Translated C-Blosc2 shuffle code included by PureHDF, BSD 3-Clause; full retained notices:
  [licenses/Blosc-shuffle.txt](Blosc-shuffle.txt).

This managed decoder runs only inside a resource-limited child process. It does not establish
NASA EMIT product support, scientific accuracy, genuine-original or installed-device acceptance.
