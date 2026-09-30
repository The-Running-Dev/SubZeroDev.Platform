# decision/2026-09-30-generic-path-settings-defect-is-configuration-error
Date: 2026-09-30
Anchor: 2026-09-30 — A generic-path settings defect is a `ConfigurationError` naming the full key
Status: accepted
SupersededBy:
StatedIn: "unit/document/20-contract § 9. Startup — `SubZeroDev.Platform.Core`, surfaced as `HostStartupError`", "unit/document/90-decisions § 2026-09-30 — A generic-path settings defect is a `ConfigurationError` naming the full key"

## Claim
Context — `/spec` #94: path 2 step 8 requires a malformed or unrecognised generic-path setting to fail startup naming the provider and the key, and names no error to carry it.

Chosen — `HostStartupError.Configuration` carrying the existing `ConfigurationError` variant that fits the defect: `MissingRequiredSetting`, `InconsistentSettings` or `InvalidSetting`. It names the full key `Platform:Identity:Bearer:<name>:<setting>`, which names the provider and the setting in one string.

Rejected — a new `HostStartupError` code for the same fault that `ConfigurationError` already names everywhere else in the host; separate provider and setting fields, when the full key is what the operator searches for.

Reversibility — cheap before a consumer ships; a variant can be added later without removing these.
