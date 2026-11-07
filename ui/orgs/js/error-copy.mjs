// Approved D-102 (UX) [review /workspace/cachd-ones/reviews/sse-error-copy-review.md md5 8c7d5274, snapshot
// /tmp/sse-llc1/sse-error-copy-review.8c7d5274.snapshot.md]: the SSE proposal (9c71950f, TLD 2026-11-04 9:49 D-51) approved
// with three changes (not_found.text, not_super_admin, FIELD.reason_length); everything else as written. MK9
// (09-location-method.html md5 90d2a8fa, D-103) draws copy only for 409 concurrency_conflict and 422 tenant_bound_elsewhere;
// those two stay in location-sign-in.html as drawn. Codes: spec §5.1 / §5.2 (contract/v1.1-S2a-location-login-config.md).
// DEC-107 [PM t149u/t150u; TLD 2026-11-04 16:32]: the reason length comes from deployment config, so the reason_length copy
// is built from the numbers the API returns (UX D-106, 09-location-method.html md5 2b17464c): reasonLengthCopy(min, max).
// The server's rule is the only check; the page never hardcodes the numbers.

/** Top-level error -> { title, text }. `null` status = network failure (no response). */
export const TOP_LEVEL = Object.freeze({
  network: { title: "Couldn't reach the server.", text: "Check your connection, then try again." }, // [TLD 2026-11-05 15:08 item 2] no "Nothing was saved": not an API answer
  401: { title: "Your session has ended.", text: "Sign in again, then try again." }, // [TLD 15:08 item 2] a real 401 only. An unexpected write 3xx is UNKNOWN, not this copy.
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
  500: { title: "Something went wrong on our side.", text: "Try again in a few minutes." }, // [TLD 15:08 item 2] a 5xx is not a contract rejection
  other: { title: "Something went wrong.", text: "Try again." }, // [TLD 15:08 item 2] not a contract rejection
});

/** Page-side outcome copy, kept apart from the UX-approved D-102 set above; no API error code maps here.
 *  [TLD 2026-11-05 15:08 item 6: provisional TLD copy, no separate UX approval needed; TLD 07:54 brief a451c6cf for the first two]
 *  write_unconfirmed: an Edit save whose outcome is UNKNOWN (timeout, transport failure, unusable answer, unexpected
 *    3xx / opaqueredirect). Exact TLD 15:08 item 3 (title + text = "We couldn't confirm the save. Reload to check before
 *    trying again."). Save stays locked until a fresh successful GET of the location (reload). A 3xx is not this copy's
 *    sign-in variant and is not proof the session ended.
 *  create_unconfirmed_blocked: a Create whose outcome is UNKNOWN. [DEC-144, provisional, not UX-approved] The admin may check
 *    outside this page and then deliberately create again. The original request may still finish.
 *  create_unconfirmed_blocked_session: the same unknown-create copy plus the sign-in line, only when an authorized read
 *    on this page returned 401. An unexpected redirect is not this case.
 *  create_reported_found: the admin said they found the location. Not a server-verified success and not a location id.
 *  history_unavailable: the history GET failed (soft copy, verbatim). */
export const FETCH_COPY = Object.freeze({
  write_unconfirmed: { title: "We couldn't confirm the save.", text: "Reload to check before trying again." },
  // Retained string. Not selected for an unexpected Edit redirect (gap 3): that case uses write_unconfirmed.
  write_unconfirmed_session: { title: "We couldn\u2019t confirm the save.", text: "Sign in again, then reload to check before trying again." },
  // [DEC-144] supersedes the indefinite "remains blocked" create copy. Provisional, not UX-approved.
  create_unconfirmed_blocked: { title: "We couldn\u2019t confirm whether the location was created.", text: "Check whether it exists before trying again. The original request may still finish, so trying again could create a duplicate." },
  create_unconfirmed_blocked_session: { title: "We couldn\u2019t confirm whether the location was created.", text: "Check whether it exists before trying again. The original request may still finish, so trying again could create a duplicate. Sign in again, then check whether the location exists." },
  create_reported_found: { title: "You reported that you found the location.", text: "This is your report, not a confirmation from the server." },
  history_unavailable: { title: "Couldn't load history — try again", text: "" },
});
/** 422 field code -> message shown under the field (tenant_bound_elsewhere is MK9 copy, kept in the page). */
export const FIELD = Object.freeze({
  required: "Fill in this field.",
  display_name_invalid: "Enter a name of 1 to 100 characters.",
  name_invalid: "Enter a location name.",
  text_invalid: "Shorten this to the allowed length.",
  country_code_invalid: "Choose a country.",
  country_unknown: "Choose a country from the list.",
  country_retired: "Choose a country from the list.",
  time_zone_unknown: "Choose a time zone from the list.",
  time_zone_retired: "Choose a time zone from the list.",
  phone_invalid: "Enter a valid phone number.",
  email_invalid: "Enter an email address with one @, or leave it blank.",
  confirmation_not_applicable: "This confirmation does not apply to the selected country.",
  unused_confirmation_required: "Confirm that this field is not used for the selected country, or enter a value.",
  field_not_used: "Leave this blank. This country does not use it.",
  client_id_format: "Choose a client.",
  tenant_count: "Enter one Microsoft tenant ID.",
  tenant_id_format: "Enter the tenant ID in the format 00000000-0000-0000-0000-000000000000 (not all zeros).",
  field_not_allowed: "A tenant ID can't be used with username and password sign-in.",
  fallback_not_applicable: "Backup sign-in is only for Microsoft sign-in.",
  // reason_length: see reasonLengthCopy below (DEC-107, UX D-106; replaces D-102 change 3).
  reason_required: "Say why you are lowering sign-in security.",
  other: "Check this field.",
});

/** 422 reason_length copy with the bounds in effect (UX D-106 md5 2b17464c, DEC-107): from GET /orgs/login-config/limits
 *  (create) or the location GET's reason_min_length / reason_max_length (edit). */
export function reasonLengthCopy(min, max) { return `Enter a reason of ${min} to ${max} characters.`; }

/** UX D-108 (reviews/sse-error-copy-review.md md5 9318c2f8, TLD 2026-11-04 18:35 / 18:37): when the limits did not load and the
 *  page uses the fallback bounds (REASON_BOUNDS), it shows no numbers (the server may enforce tighter ones). */
export const REASON_HINT_FALLBACK = "Saved in the audit log. In Create: required only if you turn off the second step or turn on backup sign-in.";
export const REASON_LENGTH_FALLBACK = "This reason is too short or too long. Edit it and try again.";
// UX D-109 (review md5 1cab5e82, TLD 2026-11-04 20:18 / 21:11): when the limits did not load, Edit shows only
// "Saved in the audit log." (the "In Create: ..." sentence is for Create only); Create keeps REASON_HINT_FALLBACK.
export const REASON_HINT_FALLBACK_EDIT = "Saved in the audit log.";
export function reasonHintFallback(create) {
  return create ? REASON_HINT_FALLBACK : REASON_HINT_FALLBACK_EDIT;
}

/** X-Request-ID display (approved D-102): own last line of every top-level error banner, never under a field; shown on every error banner so support can find the server log line (`request_id=<value>`). */
export function requestIdText(id) { return id ? `Request ID: ${id}` : ""; }
