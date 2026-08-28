# Glossary

- **DocGuid** — GUID identifying one document being processed. Presence of it on a log event routes the event to the document-log path instead of console.
- **Document Log** — per-document JSON-lines file in that document's blob container. Viewed by an in-house viewer; never sent to New Relic.
- **General Log** — log events without a DocGuid; console sink -> k8s aggregator -> New Relic.
- **Envelope** — one Storage Queue message packing many document log lines (mixed DocGuids allowed).
- **Document Log Handler** — the consumer service draining the log queue into Document Logs.
- **Poison Queue** — side queue receiving an envelope after 5 failed dequeues.
