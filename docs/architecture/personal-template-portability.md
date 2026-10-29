# Personal templates and publication governance (P24)

The reusable payload is the existing `OrganizationResearchDefinition`: the same seven
typed criteria used by Personal Research, organization claims and mission preparation.
The portable envelope adds format/version, name, description, declared attribution,
capability declarations, limitations, original card schema and a SHA-256 integrity
check. It contains no flight, observation, source file, claim authority or credential.
Attribution is self-declared. A digest establishes consistency, not authenticated
authorship, scientific validity or organizational approval.

## Reuse audit

Personal copies use `MissionStore.SaveResearchCard` and the existing immutable SQLite
card revisions. Validation uses the existing flight-regime, procedure, atmospheric,
transect, survey, observation and experimental-sensor validators. Existing Guided and
Advanced editors, preparation, acquisition, attempts, evaluation and history remain
authoritative. Imports and clones receive independent local identities and never
inherit claims. No new evidence archive or evaluator is introduced.

The organization workflow uses the existing PostgreSQL database, Identity membership,
antiforgery/origin protection, command receipts, advisory membership lock, immutable
definition revisions and audit events. New rows contain proposals, editable research
drafts, publication reviews and the selected publishing policy only. Published custom
definitions keep their supported underlying type; campaigns reference the immutable
published template revision. Existing opportunity, claim, mission and evidence paths
therefore execute the exact typed definition without a parallel execution path.

## Portable file v1

Format: `AeroScience.ResearchTemplate`, version 1, UTF-8 JSON, maximum 16 KiB and
16 nesting levels. Unknown fields (including nested future conditions), duplicate
keys, unsupported versions/types, quoted numeric tokens, numeric enum values, executable content, filesystem
paths and invalid numeric/model criteria fail closed. The original imported file is
never modified. SHA-256 covers canonical camel-case JSON with an empty `sha256` field.
Capability declarations and limitations must match the supported typed definition.
Exports project permitted definition fields only, never serialize a whole personal
card, mission, scientific source, result or organization connection.

## Publishing migration decision

Existing installations default to **Legacy** policy revision 0: administrator-only
campaign publication and manager/admin opportunity publication remain unchanged.
New reusable-template publication requires an administrator under this default.
An administrator may explicitly select **Direct Manager Publication** or
**Administrator Approval Required**. No reviewer-only role gains publishing power.
The latter policy requires a different active administrator to approve the exact
saved author/revision/hash under the current policy revision. Draft edits, policy
changes and revoked approvers invalidate approval. Publication records separately
identify author, approver and publisher. Existing immutable publications and claims
remain bound to their original hashes; no mid-flight adoption is introduced.

## Proposal privacy

Desktop proposals carry only the previewed portable definition to the selected
verified server using a URL fragment. The portal removes the fragment, verifies the
destination identity, requires sign-in and explicit confirmation, and submits using
its existing browser cookie, Origin and antiforgery protections. This does not widen
Read Published, Manage Own Claims or Submit Evidence desktop grants. A proposal is
private to its author and authorized managers/admins. Accepting for drafting makes
an independent organizational copy; it is not publication or scientific approval.
