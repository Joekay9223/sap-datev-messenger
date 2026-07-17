# Public repository boundary

This tree is the sanitized public code baseline. It intentionally excludes customer data and private operational evidence.

## Never commit

- databases, invoices, PDFs, mailbox exports, logs, screenshots, or live acceptance reports
- customer names, addresses, tax identifiers, supplier data, SAP document IDs, or internal user accounts
- private IP addresses, hostnames, UNC paths, Tailscale inventories, certificate material, or deployment paths
- API keys, OAuth credentials, service-account files, passwords, or DATEV schemas

Use local untracked configuration for company-specific deployment. Keep production evidence and deployment artifacts in a separate private repository.
