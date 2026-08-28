# 07 - 2M drain run

Type: task
Status: resolved
Blocked by: 05, 06

## Question

Two measurements, steady-state first:
1. KEEP-UP (primary): pace the generator at RATE lines/s (default 10_000) for several
   minutes; success = queue depth stays bounded near zero the whole time. Needs a RATE
   knob in LoadGenerator.
2. BURST DRAIN (secondary): pre-fill 2M lines (consumers stopped or full-rate produce),
   measure drain wall-clock. Minutes, not hours.
Record numbers in the map. Note the Azurite single-node ceiling from ticket 01.

## Answer

Both acceptance runs passed 2026-08-28 (laptop, Azurite, 4 consumer replicas x 4 pipelines).

1. KEEP-UP: 1.5M lines at a paced 5,000 lines/s (10% big docs at 2,000 lines) for ~5 min.
   Max queue depth the whole run: 20 messages; mostly 0. Poison: 0. The pipe holds at
   ~36x the average rate of a 20k-doc peak day.
2. BURST DRAIN: consumers held for 75 s while the producer enqueued 2M lines
   (3,200 envelopes, ~26 s). Backlog drained to zero in ~67 s. Blobs: 5,200 docs, correct.

Knobs used (all in apphost.cs env): DOCS, LINES_PER_DOC=200, PARALLEL_DOCS=200,
RATE (0=max), MAX_LINES, BIG_DOC_PCT=10, BIG_DOC_LINES=2000, START_DELAY_SEC.
Caveat stands: Azurite is a single Node process — these are lower bounds on mechanics,
not Azure throughput benchmarks.
