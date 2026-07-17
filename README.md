# NovaNein

NovaNein is a Windows-oriented document workflow service for reviewing invoice PDFs, reconciling them with SAP Business One, and producing validated DATEV packages.

This repository is a sanitized public code baseline. It contains no customer database, invoices, certificates, credentials, production endpoints, or operational evidence. All company, mailbox, network, and accounting values in examples are placeholders.

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

No license has been selected yet. Until a license is added, the source is not granted for reuse beyond applicable legal rights.
