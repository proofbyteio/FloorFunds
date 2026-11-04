// Approved D-102 (UX) [review /workspace/cachd-ones/reviews/sse-error-copy-review.md md5 8c7d5274, snapshot
// /tmp/sse-llc1/sse-error-copy-review.8c7d5274.snapshot.md]: the SSE proposal (9c71950f, TLD 2026-11-04 9:49 D-51) approved
// with three changes (not_found.text, not_super_admin, FIELD.reason_length); everything else as written. MK9
// (09-location-method.html md5 90d2a8fa, D-103) draws copy only for 409 concurrency_conflict and 422 tenant_bound_elsewhere;
// those two stay in location-sign-in.html as drawn. Codes: spec §5.1 / §5.2 (contract/v1.1-S2a-location-login-config.md).
// TO CONFIRM (Mohanraj, via TL): the reason length (10 to 500 characters) is a TL Decision being confirmed, so the
// reason_length copy does not state the numbers yet; the server's rule is the only check.

/** Top-level error -> { title, text }. `null` status = network failure (no response). */
export const TOP_LEVEL = Object.freeze({
  network: { title: "Couldn't reach the server.", text: "Nothing was saved. Check your connection, then try again." },
  401: { title: "Your session has ended.", text: "Nothing was saved. Sign in again, then try again." },
  forbidden: { title: "You don't have permission to do this.", text: "Nothing was saved. Ask a Super Admin or your Partner Admin." },
  not_super_admin: { title: "Only a Super Admin can make this change.", text: "Nothing was saved. Turning off the second step or allowing backup sign-in needs a Super Admin." }, // D-102 change 2
  not_found: { title: "This location wasn't found.", text: "It may have been removed, or you may not have access to it." }, // D-102 change 1
  precondition_required: { title: "The page is out of date.", text: "Nothing was saved. Reload the page, then make your change again." },
  if_match_invalid: { title: "The page is out of date.", text: "Nothing was saved. Reload the page, then make your change again." },
  payload_too_large: { title: "The request was too large.", text: "Nothing was saved. Shorten the reason or the names, then try again." },
  request_timeout: { title: "The request took too long.", text: "Nothing was saved. Try again." },
  malformed_body: { title: "The request couldn't be read.", text: "Nothing was saved. Reload the page, then try again." },
  validation_failed: { title: "Check the highlighted fields.", text: "Nothing was saved." },
  reason_required: { title: "A reason is required.", text: "Nothing was saved. Say why you are lowering sign-in security." },
  500: { title: "Something went wrong on our side.", text: "Nothing was saved. Try again in a few minutes." },
  other: { title: "Something went wrong.", text: "Nothing was saved. Try again." },
});

/** 422 field code -> message shown under the field (tenant_bound_elsewhere is MK9 copy, kept in the page). */
export const FIELD = Object.freeze({
  required: "Fill in this field.",
  display_name_invalid: "Enter a name of 1 to 100 characters.",
  name_invalid: "Enter a location name.",
  tenant_count: "Enter one Microsoft tenant ID.",
  tenant_id_format: "Enter the tenant ID in the format 00000000-0000-0000-0000-000000000000 (not all zeros).",
  field_not_allowed: "A tenant ID can't be used with username and password sign-in.",
  fallback_not_applicable: "Backup sign-in is only for Microsoft sign-in.",
  // D-102 change 3. TO CONFIRM (Mohanraj): 10 to 500 characters; once confirmed this becomes "Enter a reason of 10 to 500 characters."
  reason_length: "This reason is too short or too long. Change it, then try again.",
  reason_required: "Say why you are lowering sign-in security.",
  other: "Check this field.",
});

/** X-Request-ID display (approved D-102): own last line of every top-level error banner, never under a field; shown on every error banner so support can find the server log line (`request_id=<value>`). */
export function requestIdText(id) { return id ? `Request ID: ${id}` : ""; }
