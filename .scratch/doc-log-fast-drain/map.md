# Map: doc-log-fast-drain

Label: wayfinder:map

## Destination

A spec plus a working local Aspire simulation in this repo that proves the new design:
Serilog sink packs DocGuid log lines into Azure Storage Queue messages (Azurite),
parallel consumers append JSON-lines to per-document Append Blobs.

Success reframed 2026-08-28 (user): the goal is a PIPE, not a faster funnel —
at a sustained produce rate (e.g. 10k logs/s) consumers keep up, queue depth stays
bounded near zero. Fast drain of a 2M backlog is the secondary/bonus test (pod
restarts, KEDA lag).

## Notes

- Execution is carried into this map by user choice (destination includes the working sim, not just a spec).
- Domain glossary lives in CONTEXT.md at repo root.
- AppHost wiring: consult the aspireify / aspire skills. Keep it lazy (ponytail active).
- No git repo here; local markdown tracker per issue-tracker-local.md. Research findings land as files under .scratch/doc-log-fast-drain/research/, linked from tickets.

### Settled during charting (grilling, 2026-08-28)

- Ordering: per-DocGuid only, and restored at READ time — messages carry a producer timestamp; the viewer sorts. Consume-time ordering constraint is deleted. Consumers need no per-doc affinity.
- Queue tech: Azure Storage Queue stays. No Service Bus.
- Storage format: Append Blob + JSON-lines per document. Replaces download-modify-upload of a JSON array.
- Producer side may change: sink buffers ~1-2s, flushes in batches, flushes on graceful shutdown. Hard-crash loss window accepted ("minimal loss", not strict).
- Packing: many log lines per queue message (~48KB usable), mixed DocGuids per message OK.
- Delivery: at-least-once; rare duplicate lines OK, dedupe at read time.
- No old-design baseline in the sim; user knows old speed. Success = 2M lines drain in minutes.
- Strawman load: 2M lines, ~20k docs (~100 lines/doc). Replicas via Aspire WithReplicas; no KEDA locally. Poison queue after 5 dequeues.

### Production stats (user, 2026-08-28)

- ~6,000 docs per working day (9-5), ~200 log lines/doc => ~1.2M lines/day, avg ~42 logs/s.
- A doc takes ~2 min end to end through 4 pipeline stages (ingestion, extraction,
  enrichment, submission) - so ~4 distinct producer instances (src ids) write into one
  doc's log, out of order. Read-time sort by ts + (src,seq) is what merges them; spec requirement, not incidental.
- Stages autoscale on CPU/memory. Old system's 2M+ backlog = funnel symptom, not input size.
- Measured sim capacity so far: ~2,700 lines/s sustained on laptop Azurite (~64x avg day rate).

### Ordering under scaled-out producers (Q&A 2026-08-28)

- seq is only compared within one src (producer instance); cross-pod order comes from ts alone. Duplicate processing of a doc on two pods appends both runs' lines (different src) — (src,seq) dedupe removes transport duplicates only, by design; a doc processed twice shows both runs, which is correct for troubleshooting.
- Known footnote, accepted: cross-pod order trusts wall clocks (NTP skew = ms). If it ever matters, fix display-side (show/group by src), not pipeline-side.

## Decisions so far

<!-- one line per closed ticket -->

- [01 - Storage mechanics research](issues/01-storage-mechanics-research.md) — All load-bearing storage/Serilog assumptions confirmed (32-msg dequeue, 48 KiB payload, atomic appends, 50k-block cap distant, IBatchedLogEventSink in Serilog 4 core); one flag: Azurite lists Concurrent Append as limited, so sim timing is a lower bound, not an Azure benchmark.
- [02 - Message envelope and record schema](issues/02-message-envelope-schema.md) — record: ts/level/msg/ex/docGuid/src/seq JSON-lines; envelope: {v,lines} gzip+base64; viewer sorts by ts, tie-break+dedupe by (src,seq)
- [03 - Write the spec](issues/03-write-spec.md) — [spec.md](spec.md) written: full design, schemas, consumer loop, tuning defaults, 2M acceptance run
- [04 - Aspire AppHost wiring](issues/04-apphost-wiring.md) — apphost.cs wired: Azurite + doc-logs/doc-logs-poison queues + doc-logs container + producer + consumer x4; aspire start validated clean
- [05 - Serilog sink + load generator](issues/05-sink-and-producer.md) — built + validated: 2M lines packed into gzip envelopes and enqueued in 13.3s, zero drops
- [06 - Parallel consumer](issues/06-parallel-consumer.md) — built + validated end-to-end: 2M lines produced and consumed in ~12.4 min with queue depth bounded ~100 (consumers keep pace live); 20k blobs, 0 poison, sampled files all valid
- [07 - 2M drain run](issues/07-drain-run.md) — PASSED both: 5,000 lines/s sustained with max queue depth 20 (keep-up), and a 2M-line backlog drained in ~67 s (burst)
- [08 - Log read API](issues/08-log-read-api.md) — paged, deduped, ts-ordered GET api/documents/{docGuid}/logs on port 27080, validated live; one shared container with docGuid prefixes

## Not yet specified

(none — all tickets closed, destination reached 2026-08-28)


## Out of scope

- Production k8s/KEDA rollout and migration of the existing system.
- The general (non-DocGuid) console -> New Relic log path.
- The document-log viewer UI itself (its API slice became ticket 08).
- Migrating existing JSON-array log files to the new format.
