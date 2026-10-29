# Optional organization recovery image

The optional P26 operations image adds PostgreSQL 17 client tools (PostgreSQL License)
and restic 0.19.1 (BSD 2-Clause License, with its upstream Go dependency notices).
It does not change scientific acquisition or include simulator components.

Source and pinned release:
- https://github.com/restic/restic/tree/v0.19.1
- https://github.com/restic/restic/releases/tag/v0.19.1
- https://www.postgresql.org/about/licence/

This is a development deployment candidate. Complete transitive binary-license/SBOM,
vulnerability, signature and distribution acceptance before publishing an official
recovery/container release. No production image or public deployment is published by P26.
