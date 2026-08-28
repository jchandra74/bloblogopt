# 08 - Log read API

Type: task
Status: resolved
Blocked by: 06

## Question

GET /api/documents/{docGuid}/logs?page_size=25&page=1 — return the doc's deduped log
entries (dedupe by (src,seq)), ordered ts asc with (src,seq) tie-break, plus a rowId.
Destination extended by user 2026-08-28 (was out of scope as "viewer app"; this is the
API slice of it).

## Answer

Built and validated 2026-08-28. [src/LogApi](../../../src/LogApi/Program.cs), minimal API
on fixed port 27080, wired in apphost with WithReference(logContainer).

GET /api/documents/{docGuid}/logs?page_size=25&page=1 ->
{ docGuid, page, pageSize, totalLines, totalPages, lines: [ {rowId, ts, level, msg, ex, src, seq} ] }
- Dedupe by (src,seq), order ts asc, tie-break (src,seq); rowId = position in the
  sorted deduped list (UI convenience; ordering is already deterministic without it).
- 404 for unknown doc. page_size clamped 1-1000.
- Validated live: steps in order, correct paging (rowIds 4,5,6 on page 2), 404 works.

Partitioning decision recorded: ONE container (doc-logs), doc = prefix {docGuid}/log.jsonl.
Container-per-doc (their current prod layout) also works via a one-line path change; one
container keeps lifecycle rules in one place.
Also: apphost resting defaults now a quick demo load (MAX_LINES=100000, START_DELAY_SEC=0).
