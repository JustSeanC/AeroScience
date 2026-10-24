# Storage limits, protection and safe cleanup

**Settings → Storage & Data** configures independent flight and Scientific Library folders.
The flight archive retains its configurable quota (default 25 GB). Scientific retention has no
source-count limit and no default application quota: available physical storage and write/recovery
headroom govern capacity. Legacy scientific cap settings are normalized to Unlimited without
removing existing data. Per-request/job bounds and network allowances remain safety controls.

Managed science usage includes SQLite DB/WAL/SHM, unique stored originals, complete products,
registered previews, metadata and owned interrupted work. It is separate from physical free space,
reservations and separately displayed backup allocation. Accounting is cached/updated for known
writes; Refresh usage reconciles external changes. Reserve checks prevent concurrent writers from
spending the same free capacity. Imports verify free space while copying as well as before publication.
Insufficient scientific-drive space suspends new science writes independently of flight recording.
A missing configured drive never creates a replacement Library. Invalid settings remain explicit.

Both External Data Manager views show the actual configured path, usage and physical free space.
Local Windows uses **Open Data Folder**; paired devices use **Copy Path**. Source details can open
only verified managed source folders, never arbitrary browser-supplied paths. See the
[Library guide](../scientific-library.md#storage-location-and-library-scale).

## Preview Cleanup

Open **Settings → Storage & Data → Storage Management → Preview Cleanup**. Summary includes eligible file count/estimated bytes, oldest/newest created dates, category/policy, current usage and expected usage. The scrollable table shows actual filename, category, mission, dataset/product, created date/age, actual size, reason and status. Expand a row for its relative managed path, regeneration dependency, last-used timestamp and hashes. Use filter, sort/mission/dataset grouping and 100-row pages. Registered cache inspection is bounded at 10,000 entries per pass.

**Eligible** means a positively registered `remote-preview-v1` PNG whose exact path/hash/size match and whose complete original product still exists. **Protected** indicates a pinned mission/dataset; **In Use** indicates an active managed reader/writer; **Excluded** means regeneration/safe identity cannot be established. Original imports, completed observations/products, flight rows, saved plans/presets, metadata, unknown files and backups are never automatic cleanup targets. An original dataset is not being deleted because its preview is eligible.

Choose **Clean Eligible Files**, review final file count/estimate and confirm, or **Cancel**. Only that preview's eligible snapshot is considered, including filtered-out eligible rows (the confirmation states this). Every candidate is revalidated; pinned/changed/busy/missing dependencies are skipped, complete original-product checksum is checked immediately before deleting the cache. New files are not silently added. After 5 minutes or a material usage change, refresh before confirming. Results distinguish removed/skipped/failed, actual bytes removed and measured free-space delta (which can differ due to filesystem allocation/other activity). Cleanup history is local in `Metadata/CleanupHistory`; automatic cleanup uses the same rules, bounded to 20 cache eligibility checks/removals per pass, without requiring a manual preview.

**Protect Mission** in history and **Protect Dataset** in product view survive restarts/migrations. Protection prevents automatic cache deletion, not a deliberate reviewed scientific-data deletion that explicitly identifies its selected scope.

## Archive clear, compaction and migration

External originals and collected mission science have separate deletion scopes. Each scientific
operation uses one expiring hashed review, Cancel and a red Delete action; no typed phrase or
second browser confirmation. Science deletion never creates a backup and purges targeted content
from registered managed copies. Shared/unavailable copies block completion rather than silently
retaining selected content. Completed independent mission results survive deletion of their external
originals; deleting collected science instead removes dependent grades/analysis. Raw flight telemetry
and external originals remain unless separately selected. See [permanent deletion](../scientific-library.md#manage-downloaded-data-safely).
The separate flight-archive clear retains its existing safeguards and explicit backup option.

Optional **Compact Flight/Science Database…** stops services, requires confirmation, lease/free-space checks, creates a verified backup, runs SQLite VACUUM and verifies logical fingerprints. It reports actual DB allocation reclaimed; backup bytes are additional. No automatic compaction runs in flight.

Storage location changes use the existing stopped-store SQLite backup/verification/migration journal and keep originals. Science migration retains the library ID, relative product/source references, pins and work ledgers. An existing destination is a conflict to resolve explicitly; nothing is merged/overwritten automatically. Interrupted `.partial` imports/unindexed products remain protected for explicit diagnostic recovery and are counted, not automatically disposed of. Preserve ledgers, original files and both stores when reporting an interruption. Do not manually copy an actively recording SQLite database.

## Automatic scientific access

Prepared provider subsets are retained scientific originals, never regenerable cache entries. Discovery/transfer budgets and conservative staging reservations are separate from actual retained usage. Flight quota, pinned automatic-cleanup protection and verified migrations remain separate from unlimited-count scientific retention. Partial downloads stay ledger-accounted for explicit review; cancellation never discards completed observations/products or a prior source. See [automatic provider limits](automatic-data.md).
