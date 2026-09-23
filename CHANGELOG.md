# Changelog

All notable changes to Subscrio Audit Log are recorded here.

## [Unreleased]

## [0.5.0] - 2026-09-23

### Added

- Audit records for add-on attachments, usage reporting, and credit grants, consumption, and adjustments.
- Timed override expiration in audit details.

### Changed

- TypeScript requires Subscrio 0.5.x; .NET requires Subscrio.Core 0.5.1 or later.
- Tests use published core packages and cover hook completeness, rejected operations, and idempotent retries.

## [0.4.0] - 2026-09-20

### Changed

- The TypeScript package now requires a compatible `subscrio` 0.4.x release.
- The TypeScript and .NET packages are versioned together with the 0.4.0 core release.

### Fixed

- Stripe audit rows reliably retain customer and subscription associations by using IDs supplied on the received hook payload.

### Verified

- Audit records remain compatible with customer, subscription, and Stripe after-hooks in Subscrio 0.4.0.
