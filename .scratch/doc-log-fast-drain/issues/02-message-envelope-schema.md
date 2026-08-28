# 02 - Message envelope and record schema

Type: grilling
Status: resolved

## Question

Decide the shape of (a) the packed queue message (envelope holding many log lines) and
(b) the JSON-lines record written per log line (fields: DocGuid, timestamp, sequence,
level, message, exception, ...?). What does the troubleshooting viewer need per line?

## Answer

Decided 2026-08-28 with user.

**Record** (one JSON-lines line per log event):
```json
{ "ts": "2026-08-28T10:15:30.123Z", "level": "Information", "msg": "rendered text",
  "ex": null, "docGuid": "guid", "src": "producer-instance-id", "seq": 12345 }
```
- `ts` ISO-8601 UTC. `ex` full exception ToString, null if none.
- `src` = random id per producer instance at startup; `seq` = per-instance counter.
- Viewer rule: sort by `ts`, tie-break `(src, seq)`, drop exact `(src, seq)` duplicates.
- No structured props bag for now; additive later if the viewer wants filters.

**Envelope** (one queue message): `{ "v": 1, "lines": [ ...records... ] }`,
gzipped then base64. ~5-10x more lines per message vs plain JSON;
2M lines ~ 2-4k queue messages.
