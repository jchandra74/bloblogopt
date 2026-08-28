# 06 - Parallel consumer

Type: task
Status: resolved
Blocked by: 01, 02, 04

## Question

Build the consumer: batch dequeue, unpack envelopes, append JSON-lines to per-DocGuid
Append Blobs (create on first use), poison queue after 5 dequeues, safe under many
replicas. At-least-once; duplicates tolerated.

## Answer

Built and validated 2026-08-28. [Worker.cs](../../../src/Consumer/Worker.cs):
4 dequeue pipelines per replica (x4 replicas = 16). Each: ReceiveMessages(32, vis 2min)
-> gunzip envelopes -> group lines by docGuid across the whole batch -> parallel
appends (cap 32, append-first with create-on-404, 3MiB chunks split at line
boundaries) -> parallel deletes only after all appends succeed. Poison after
DequeueCount > 5. Clients: keyed AddKeyedAzureQueue for BOTH queues + AddAzureBlobContainerClient.

Validation (full end-to-end, 20k docs x 100 lines):
- 2,000,000 lines produced AND consumed in ~12.4 min, producer-paced; queue depth
  stayed bounded at ~70-130 messages throughout = consumers keep up live (the "pipe").
- Final: queue 0, poison 0, blobs exactly 20,000.
- 20 random blobs sampled: 100 valid JSON lines each, zero duplicate (src,seq), all
  lines in the right doc's file.

Gotchas recorded:
- Registering an unkeyed AzureQueue client + a keyed one nulls the unkeyed -> use keyed for both.
- Azurite: --inMemoryPersistence conflicts with the --location arg Aspire passes; don't use.
- First cut was ~400 lines/s: create-check per doc-group + serial deletes + 1 pipeline. The
  three fixes above took it to producer speed; Azurite (single Node process) is now the ceiling.
- aspire logs streams (blocks); poll queue depth via pinned emulator ports (tools/depth.cs) instead.
- Producer emission made bursty (PARALLEL_DOCS=200) so doc-batches fatten appends; pure
  round-robin across 20k docs is adversarial and unrealistic.
