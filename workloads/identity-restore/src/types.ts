/**
 * Shapes shared by the recorder, the gap detector, the planner and the restore. The Keycloak shapes are
 * the subset of `EventRepresentation` and `AdminEventRepresentation` (Keycloak 26.8.1) this workload
 * reads; fields it does not read are kept verbatim in the stored body.
 */

export interface KcUserEvent {
  readonly id: string;
  readonly time: number;
  readonly type: string;
  readonly realmId: string;
  readonly clientId?: string;
  readonly userId?: string;
  readonly sessionId?: string;
  readonly ipAddress?: string;
  readonly details?: Readonly<Record<string, string>>;
}

export interface KcAdminEvent {
  readonly id: string;
  readonly time: number;
  /** The realm the change was made in. The realm of the admin is in `authDetails.realmId`. */
  readonly realmId: string;
  readonly authDetails?: { readonly realmId?: string; readonly clientId?: string; readonly userId?: string; readonly ipAddress?: string };
  readonly operationType: string;
  readonly resourceType?: string;
  readonly resourcePath?: string;
  readonly representation?: string;
  readonly details?: Readonly<Record<string, string>>;
}

export type StoredEvent =
  | { readonly kind: "user"; readonly id: string; readonly timeMs: number; readonly body: KcUserEvent }
  | { readonly kind: "admin"; readonly id: string; readonly timeMs: number; readonly body: KcAdminEvent };

/** One recorder cycle's claim about a closed time range, both ends inclusive (Keycloak's `dateFrom` and
 *  `dateTo` are both inclusive, in milliseconds). */
export interface RecordWindow {
  readonly seq: number;
  readonly fromMs: number;
  readonly throughMs: number;
  readonly status: "covered" | "gap";
  readonly reason: string | null;
  readonly lateCount: number;
  /** The recorder's own `CLIENT_LOGIN`, requested at the start of the cycle and found in Keycloak's store
   *  with a time after `throughMs`: proof that event storage was on and had committed past the window.
   *  `null` on a gap window where it was not found. */
  readonly canaryId: string | null;
  readonly canaryMs: number | null;
  /** When the recorder wrote the window, by the record server's clock. A window written after the old
   *  Keycloak store was retired may have been read from the restored store, and does not count. */
  readonly polledAtMs: number;
}

export interface BackupMeta {
  readonly startedAtMs: number;
  readonly finishedAtMs: number;
  readonly sha256: string;
  readonly bytes: number;
}

export interface RestoreLogEntry {
  readonly restoreId: string;
  readonly phase: "held" | "applied" | "reopened" | "refused" | "released";
  readonly atMs: number;
  readonly detail: Readonly<Record<string, unknown>>;
}

export const RECOVERY_CODES = "recovery-authn-codes";
export const PASSKEY = "webauthn-passwordless";
