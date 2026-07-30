# QuotaBeacon

QuotaBeacon is a local-first Windows tray application for monitoring the remaining usage quotas of AI services.

The initial release targets Claude and OpenAI Codex. Each provider works independently, so users can enable Claude, Codex, or both.

## Project status

QuotaBeacon is currently in the product-design stage. See [docs/product-requirements.md](docs/product-requirements.md) for the agreed requirements and open design decisions.

## Product principles

- Local-first processing
- Explicit user consent for credential discovery
- Independent provider modules
- No telemetry or credential transmission to third parties
- Clear freshness and error information

