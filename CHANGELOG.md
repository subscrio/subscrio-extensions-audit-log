# Changelog

All notable changes to Subscrio Audit Log are recorded here.

## [Unreleased]

## [0.4.0] - 2026-09-20

### Changed

- The TypeScript package now requires a compatible `subscrio` 0.4.x release.
- The TypeScript and .NET packages are versioned together with the 0.4.0 core release.

### Fixed

- Stripe audit rows reliably retain customer and subscription associations by using IDs supplied on the received hook payload.

### Verified

- Audit records remain compatible with customer, subscription, and Stripe after-hooks in Subscrio 0.4.0.
