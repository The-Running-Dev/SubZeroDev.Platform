# I-OB1
Kind: invariant
Status: active
Anchor: I-OB1
Enforcement: code
Consumes:
Exposes:
Binds:
Live:
Archival:
Questions:
Work:
Evidence:

## Statement
An absent OTLP endpoint starts no exporter; a present invalid endpoint aborts both registration paths with the same `ConfigurationError.InvalidSetting`; a validly configured exporter failure never propagates to application work (Owner: Observability, Hosting.) Enforced by code — standalone and hosted configuration tests, plus the existing blocked-export test.
