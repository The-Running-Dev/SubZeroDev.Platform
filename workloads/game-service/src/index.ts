/** The workload's module surface — every signature `design/20-contract.md` names for it. */
export { canonicalEncode } from "./canonical.js";
export { loadContract, findRow, statusFor } from "./contract.js";
export { validateRequest, validateResponse } from "./validate.js";
export { compose } from "./compose.js";
export { createDispatcher } from "./dispatch.js";
export { buildHttpSurface } from "./http-surface.js";
export { startWorkload, createProbeSurface, CONTRACT_PATH_VARIABLE } from "./lifecycle.js";
export * from "./types.js";
