# SAP-Datev Messenger

SAP-Datev Messenger is a Windows-oriented document workflow service for reviewing invoice PDFs, reconciling them with SAP Business One, and producing validated DATEV packages.

This repository is a sanitized public code baseline. It contains no customer database, invoices, certificates, credentials, production endpoints, or operational evidence. All company, mailbox, network, and accounting values in examples are placeholders.

The source and solution retain the existing NovaNein technical identifiers for compatibility; the public project name is SAP-Datev Messenger.

## Capabilities

- PDF intake with hash-based deduplication and audit events
- Read-only SAP Business One adapters with separately gated write paths
- Human review and approval workflows for invoice proposals
- OpenAI document interpretation with deterministic local reconciliation
- DATEV v6 package generation and safety validation
- Optional Windows client, mTLS, backup, and transfer-bridge components

## Safety defaults

SAP writes, mailbox ingestion, supplier writes, and direct DATEV transfer are disabled by default. Secrets and production paths must be supplied through local deployment configuration and must never be committed. DATEV XSD files are not distributed by this repository.

## Build and test

Requirements: .NET 8 SDK and Node.js.

```sh
dotnet restore NovaNein.sln
dotnet build NovaNein.sln --no-restore
dotnet test NovaNein.sln --no-build --no-restore
npm test
```

## Configuration

Start with the files under `config/`. Replace the placeholder company, SAP, mailbox, network, and DATEV values in a local, untracked configuration file.

## Repository boundary

Production logs, live acceptance reports, screenshots, customer-specific fixtures, generated packages, database files, certificates, DATEV schemas, and deployment credentials belong in a separate private operations repository.

## License

This project is licensed under the [PolyForm Noncommercial License 1.0.0](LICENSE).

Private and other noncommercial use is permitted under the license. Use in a business, for commercial advantage, or for commercial application is not permitted without separate permission from the copyright holder. Third-party components remain subject to their own licenses.
