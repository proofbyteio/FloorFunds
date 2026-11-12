using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Catalog.Contracts;
using Catalog.Contracts.Doubles;
using Catalog.Transport;

Console.WriteLine("Catalog.Transport.Checks: synthetic in-process HTTP only. Not a listener, database, runtime, relay, or acceptance.");

var pass = 0;
var fail = new List<string>();
void Check(string name, bool ok)
{
    Console.WriteLine((ok ? "PASS " : "FAIL ") + name);
    if (ok) pass++;
    else
    {
        fail.Add(name);
        Console.WriteLine("  status=" + Captured.Response.StatusCode + " body=" + Captured.Response.Body);
    }
}

var root = RepoRoot();
var schemaBytes = File.ReadAllBytes(Path.Combine(root, "contract", "audit-events.schema.json"));
var errorsBytes = File.ReadAllBytes(Path.Combine(root, "contract", "errors.md"));
Check("frozen audit schema hash", Sha256(schemaBytes) == "ce0b904fcc5069d9e727e62600efabca311c8a407297ed5dbc96f605ae807b1b" && schemaBytes.Length == 39962);
Check("frozen errors.md hash", Sha256(errorsBytes) == "dd15977e50be88364fa758c8c8c32618539ddfaed43e31205e4b9b65617cd80b" && errorsBytes.Length == 8473);

var transportDir = Path.Combine(root, "src", "Catalog.Transport");
var transportText = string.Join("\n", Directory.GetFiles(transportDir, "*.cs", SearchOption.AllDirectories).Select(File.ReadAllText));
var transportProject = File.ReadAllText(Path.Combine(transportDir, "Catalog.Transport.csproj"));
Check("transport has no package reference and no host or database types",
    !transportProject.Contains("PackageReference", StringComparison.Ordinal)
    && transportProject.Contains("Catalog.Contracts.csproj", StringComparison.Ordinal)
    && !transportText.Contains("PackageReference", StringComparison.Ordinal)
    && !transportText.Contains("Npgsql", StringComparison.Ordinal)
    && !transportText.Contains("Admin.Api", StringComparison.Ordinal)
    && !transportText.Contains("Auth.Data", StringComparison.Ordinal)
    && !transportText.Contains("DbContext", StringComparison.Ordinal)
    && !transportText.Contains("audit_outbox", StringComparison.Ordinal)
    && !transportText.Contains("Kestrel", StringComparison.Ordinal)
    && !transportText.Contains("HttpListener", StringComparison.Ordinal)
    && !transportText.Contains("TcpListener", StringComparison.Ordinal)
    && !transportText.Contains("HttpClient", StringComparison.Ordinal)
    && !transportText.Contains("WebApplication", StringComparison.Ordinal)
    && !transportText.Contains("Migration", StringComparison.Ordinal)
    && !transportText.Contains("economic_role", StringComparison.Ordinal)
    && !transportText.Contains("variant_note", StringComparison.Ordinal)
    && !transportText.Contains("supersedes", StringComparison.Ordinal)
    && !transportText.Contains("sa_only", StringComparison.OrdinalIgnoreCase)
    && !transportText.Contains(FrozenAuditSchemaBoundary.RejectedSentinel, StringComparison.Ordinal)
    && !transportText.Contains(FrozenAuditSchemaBoundary.DurableAcceptedNotClaimed, StringComparison.Ordinal));

var clock = new ManualClock(DateTimeOffset.Parse("2026-11-08T19:55:00Z"));
var repo = new InMemoryCatalogRepository();
var service = new CatalogDomainService(repo, clock, new CountingIds());
var http = new CatalogHttpTransport(service);
var sa = new CatalogPrincipal(G(1), CatalogRole.SuperAdmin, null);
var pa = new CatalogPrincipal(G(2), CatalogRole.PartnerAdmin, G(20));
var otherPa = new CatalogPrincipal(G(3), CatalogRole.PartnerAdmin, G(21));
var cashier = new CatalogPrincipal(G(4), CatalogRole.Cashier, null);
var saCtx = Ctx(sa);
var paCtx = Ctx(pa);
var otherCtx = Ctx(otherPa);

Check("malformed framing writes nothing", Refusal(http, saCtx, "not http", "malformed_http") && repo.EntryCount == 0);
Check("a query string is rejected and is not a filter", Refusal(http, saCtx, "GET /admin/catalogs/fee_type/entries?owner_partner_id=" + G(99).ToString("D") + " HTTP/1.1\r\nHost: catalog.test\r\n\r\n", "unknown_query")
    && !Captured.Response.Body.Contains(G(99).ToString("D"), StringComparison.Ordinal) && repo.EntryCount == 0);
Check("HTTP/1.0 is rejected", Refusal(http, saCtx, "GET /admin/catalogs/fee_type/entries HTTP/1.0\r\nHost: catalog.test\r\n\r\n", "malformed_http"));
Check("chunked encoding is rejected", Refusal(http, saCtx, "POST /admin/catalogs/fee_type/entries HTTP/1.1\r\nHost: catalog.test\r\nTransfer-Encoding: chunked\r\n\r\n0\r\n\r\n", "malformed_http"));
Check("duplicate content-length is rejected", Refusal(http, saCtx, "POST /admin/catalogs/fee_type/entries HTTP/1.1\r\nHost: catalog.test\r\nContent-Length: 2\r\nContent-Length: 2\r\n\r\n{}", "malformed_http"));
Check("a short content-length does not read a smuggled body", Refusal(http, saCtx, "POST /admin/catalogs/fee_type/entries HTTP/1.1\r\nHost: catalog.test\r\nContent-Type: application/json\r\nContent-Length: 2\r\n\r\n{}{}", "malformed_http") && repo.EntryCount == 0);
var bom = Encoding.ASCII.GetBytes("GET /admin/catalogs/fee_type/entries HTTP/1.1\r\nHost: catalog.test\r\n\r\n");
var withBom = new byte[bom.Length + 3];
withBom[0] = 0xEF; withBom[1] = 0xBB; withBom[2] = 0xBF;
bom.CopyTo(withBom, 3);
var bomResponse = http.Handle(saCtx, withBom);
Check("a byte order mark is rejected", bomResponse.StatusCode == 400 && Field(bomResponse, "code") == "malformed_http" && repo.EntryCount == 0);
var invalidUtf8 = Encoding.ASCII.GetBytes("POST /admin/catalogs/fee_type/entries HTTP/1.1\r\nHost: catalog.test\r\nContent-Type: application/json\r\nIdempotency-Key: utf\r\nContent-Length: 1\r\n\r\n");
var utf8 = new byte[invalidUtf8.Length + 1];
invalidUtf8.CopyTo(utf8, 0);
utf8[^1] = 0xFF;
var utfResponse = http.Handle(saCtx, utf8);
Check("invalid body bytes are rejected", utfResponse.StatusCode == 400 && Field(utfResponse, "detail") == "invalid_utf8" && repo.EntryCount == 0);

var actorHeader = "00000000-0000-0000-0000-000000000099";
var headerProbe = Send(http, cashier, "POST", "/admin/catalogs/fee_type/entries", "{\"code\":\"late\",\"display_name\":\"Late\"}", ("X-Actor-User-Id", actorHeader), ("Idempotency-Key", "header-actor"));
Check("an actor header cannot impersonate and its value is not echoed", headerProbe.StatusCode == 400 && Field(headerProbe, "code") == "unknown_header" && Field(headerProbe, "detail") == "x-actor-user-id"
    && !headerProbe.Body.Contains(actorHeader, StringComparison.Ordinal) && !headerProbe.Raw.Contains(actorHeader, StringComparison.Ordinal) && repo.EntryCount == 0);
var authProbe = Send(http, cashier, "GET", "/admin/catalogs/fee_type/entries", null, ("Authorization", "Bearer " + actorHeader));
Check("authorization material is not the principal and is not echoed", authProbe.StatusCode == 400 && Field(authProbe, "code") == "unknown_header"
    && !authProbe.Body.Contains(actorHeader, StringComparison.Ordinal) && !authProbe.Raw.Contains("Bearer", StringComparison.Ordinal) && repo.EntryCount == 0);
Check("delete is not a route", Send(http, sa, "DELETE", "/admin/catalogs/fee_type/entries/" + G(80).ToString("D"), null).StatusCode == 400
    && Field(Captured.Response, "code") == "method_not_allowed" && repo.EntryCount == 0);
Check("an unknown family fails closed", Send(http, sa, "GET", "/admin/catalogs/widget/entries", null).StatusCode == 400 && Field(Captured.Response, "code") == "invalid_family");
Check("a get body is rejected", Send(http, sa, "GET", "/admin/catalogs/fee_type/entries", "{\"owner_partner_id\":\"" + G(99).ToString("D") + "\"}").StatusCode == 400
    && Field(Captured.Response, "code") == "unexpected_body" && repo.EntryCount == 0);
Check("an idempotency key is not a read replay", Send(http, sa, "GET", "/admin/catalogs/fee_type/entries", null, ("Idempotency-Key", "read-key")).StatusCode == 400
    && Field(Captured.Response, "code") == "idempotency_not_applicable" && repo.IdempotencyCount == 0);

foreach (var family in new[] { "catalog_release", "id_document_type", "currency" })
{
    var blocked = Send(http, sa, "POST", "/admin/catalogs/" + family + "/entries", "{\"code\":\"x\",\"display_name\":\"X\"}", ("Idempotency-Key", "unresolved-" + family));
    Check($"{family} create stays unavailable and writes nothing", blocked.StatusCode == 503 && Field(blocked, "gate") == CatalogGates.CreateDtoUnresolved
        && Field(blocked, "detail") == "required_fields_unresolved" && Field(blocked, "wrote") == "false" && repo.EntryCount == 0 && repo.IntentCount == 0);
}
var manufacturer = Send(http, pa, "POST", "/admin/catalogs/manufacturer/entries", "{\"code\":\"acme\",\"display_name\":\"Acme\"}", ("Idempotency-Key", "mfr"));
Check("manufacturer create fields stay unclosed", manufacturer.StatusCode == 503 && Field(manufacturer, "detail") == "create_fields_not_closed" && repo.EntryCount == 0);
var impersonated = Send(http, pa, "POST", "/admin/catalogs/fee_type/entries", "{\"code\":\"late\",\"display_name\":\"Late\",\"owner_partner_id\":\"" + G(99).ToString("D") + "\"}", ("Idempotency-Key", "imp"));
Check("a create body cannot choose an owner", impersonated.StatusCode == 400 && Field(impersonated, "code") == "authority_field_rejected" && Field(impersonated, "detail") == "owner_partner_id"
    && !impersonated.Body.Contains(G(99).ToString("D"), StringComparison.Ordinal) && repo.EntryCount == 0);
var classified = Send(http, pa, "POST", "/admin/catalogs/fee_type/entries", "{\"code\":\"late\",\"display_name\":\"Late\",\"economic_role\":\"unknown\"}", ("Idempotency-Key", "fee-class"));
Check("fee economic_role is an unknown field and stores no default", classified.StatusCode == 400 && Field(classified, "code") == "unknown_field" && Field(classified, "detail") == "economic_role"
    && !classified.Body.Contains("default", StringComparison.OrdinalIgnoreCase) && repo.EntryCount == 0);
var variant = Send(http, sa, "POST", "/admin/catalogs/fee_type/entries", "{\"code\":\"late\",\"display_name\":\"Late\",\"variant_note\":\"x\"}", ("Idempotency-Key", "variant"));
var supersedes = Send(http, sa, "POST", "/admin/catalogs/fee_type/entries", "{\"code\":\"late\",\"display_name\":\"Late\",\"supersedes\":\"y\"}", ("Idempotency-Key", "super"));
Check("variant_note and supersedes are unknown fields", variant.StatusCode == 400 && Field(variant, "detail") == "variant_note" && supersedes.StatusCode == 400 && Field(supersedes, "detail") == "supersedes" && repo.EntryCount == 0);
var sentinelBody = Send(http, sa, "POST", "/admin/catalogs/fee_type/entries", "{\"code\":\"late\",\"display_name\":\"" + FrozenAuditSchemaBoundary.RejectedSentinel + "\"}", ("Idempotency-Key", "sent"));
Check("the unapproved sentinel is rejected and not stored", sentinelBody.StatusCode == 400 && Field(sentinelBody, "code") == "schema_version_rejected"
    && !sentinelBody.Body.Contains(FrozenAuditSchemaBoundary.RejectedSentinel, StringComparison.Ordinal) && repo.EntryCount == 0);
var sentinelKey = Send(http, sa, "POST", "/admin/catalogs/fee_type/entries", "{\"code\":\"late\",\"display_name\":\"Late\",\"schema_version\":\"1.2.0\"}", ("Idempotency-Key", "sent-key"));
Check("a schema_version field is rejected", sentinelKey.StatusCode == 400 && Field(sentinelKey, "code") == "authority_field_rejected" && repo.EntryCount == 0);
Check("a write without an idempotency key writes nothing", Send(http, sa, "POST", "/admin/catalogs/fee_type/entries", "{\"code\":\"late\",\"display_name\":\"Late\"}").StatusCode == 400
    && Field(Captured.Response, "code") == "idempotency_key_required" && repo.EntryCount == 0);
Check("a cashier cannot create", Send(http, cashier, "POST", "/admin/catalogs/fee_type/entries", "{\"code\":\"late\",\"display_name\":\"Late\"}", ("Idempotency-Key", "cashier")).StatusCode == 403
    && Field(Captured.Response, "code") == "no_management_grant" && repo.EntryCount == 0);
Check("if-match is not a create precondition", Send(http, sa, "POST", "/admin/catalogs/fee_type/entries", "{\"code\":\"late\",\"display_name\":\"Late\"}", ("Idempotency-Key", "match"), ("If-Match", "\"1\"")).StatusCode == 400
    && Field(Captured.Response, "code") == "precondition_not_applicable" && repo.EntryCount == 0);

var created = Send(http, sa, "POST", "/admin/catalogs/fee_type/entries", "{\"code\":\"LATE\",\"display_name\":\"Late fee\"}", ("Idempotency-Key", "fee-global"));
var createdJson = Json(created);
var globalId = createdJson.GetProperty("entry_id").GetGuid();
Check("SA fee create is names only and the intent is not emitted or durable", created.StatusCode == 201 && created.Raw.StartsWith("HTTP/1.1 201 Created\r\n", StringComparison.Ordinal)
    && created.Headers["X-Catalog-Mapping"] == CatalogHttpMapping.HeaderValue
    && created.Headers["Cache-Control"] == "no-store"
    && !created.Headers.ContainsKey("Date")
    && Field(created, "wrote") == "true"
    && Field(created, "audit_intent_status") == AuditAllowlist.IntentStatus
    && Field(created, "audit_emitted") == "false"
    && Field(created, "proposed_event_name") == ProposedCatalogEvents.Created
    && createdJson.GetProperty("entry").GetProperty("entry_class").GetString() == "global"
    && !createdJson.GetProperty("entry").TryGetProperty("owner_partner_id", out _)
    && createdJson.GetProperty("entry").GetProperty("fields").EnumerateObject().Count() == 0
    && createdJson.GetProperty("entry").GetProperty("entry_activation").GetProperty("token").GetString() == "ABSENT"
    && !createdJson.GetProperty("entry").GetProperty("entry_activation").TryGetProperty("number", out _)
    && createdJson.GetProperty("entry").GetProperty("effective_availability").GetString() == "unknown"
    && !Names(createdJson).Contains("economic_role")
    && !Names(createdJson).Contains("schema_version")
    && !Names(createdJson).Contains("actor_user_id")
    && !created.Body.Contains(FrozenAuditSchemaBoundary.DurableAcceptedNotClaimed, StringComparison.Ordinal)
    && !created.Body.Contains(FrozenAuditSchemaBoundary.RejectedSentinel, StringComparison.Ordinal)
    && repo.EntryCount == 1 && repo.HistoryCount == 1 && repo.IntentCount == 1 && repo.IdempotencyCount == 1);
var beforeReplayHistory = repo.HistoryCount;
clock.UtcNow = clock.UtcNow.AddHours(2);
var replayCtx = new CatalogHttpServerContext(sa, "other-correlation", "other-request");
var replay = http.Handle(replayCtx, Message("POST", "/admin/catalogs/fee_type/entries", "{\"code\":\"LATE\",\"display_name\":\"Late fee\"}", ("Idempotency-Key", "fee-global")));
Check("the same key replays the committed result without a second history row", replay.StatusCode == 201 && replay.Body == created.Body
    && repo.HistoryCount == beforeReplayHistory && repo.IntentCount == 1 && repo.IdempotencyCount == 1
    && Json(replay).GetProperty("correlation_id").GetString() == "corr");
var conflict = Send(http, sa, "POST", "/admin/catalogs/fee_type/entries", "{\"code\":\"OTHER\",\"display_name\":\"Other\"}", ("Idempotency-Key", "fee-global"));
Check("the same key with a different body writes nothing", conflict.StatusCode == 409 && Field(conflict, "code") == "idempotency_conflict" && Field(conflict, "wrote") == "false" && repo.EntryCount == 1);
var otherActor = Send(http, pa, "POST", "/admin/catalogs/fee_type/entries", "{\"code\":\"LATE\",\"display_name\":\"Late fee\"}", ("Idempotency-Key", "fee-global"));
Check("another actor cannot reuse the key as a replay", otherActor.StatusCode == 409 && Field(otherActor, "code") == "idempotency_conflict" && repo.EntryCount == 1);
var privateFee = Send(http, pa, "POST", "/admin/catalogs/fee_type/entries", "{\"code\":\"LATE\",\"display_name\":\"Late fee\",\"description\":\"Name only\"}", ("Idempotency-Key", "fee-private"));
var privateId = Json(privateFee).GetProperty("entry_id").GetGuid();
Check("PA fee create is private, names only, and may share the global code", privateFee.StatusCode == 201 && privateId != globalId
    && Json(privateFee).GetProperty("entry").GetProperty("entry_class").GetString() == "private"
    && Json(privateFee).GetProperty("entry").GetProperty("owner_partner_id").GetGuid() == pa.PartnerId
    && Json(privateFee).GetProperty("entry").GetProperty("description").GetString() == "Name only"
    && !Names(Json(privateFee)).Contains("economic_role"));
var secondGlobal = Send(http, sa, "POST", "/admin/catalogs/fee_type/entries", "{\"code\":\"LATE\",\"display_name\":\"Late again\"}", ("Idempotency-Key", "fee-global-2"));
Check("a second global with the same code is an unresolved policy, not a uniqueness claim", secondGlobal.StatusCode == 503 && Field(secondGlobal, "detail") == "code_policy_unresolved"
    && !secondGlobal.Body.Contains("unique", StringComparison.OrdinalIgnoreCase) && repo.EntryCount == 2);
var secondPrivate = Send(http, otherPa, "POST", "/admin/catalogs/fee_type/entries", "{\"code\":\"LATE\",\"display_name\":\"Other private\"}", ("Idempotency-Key", "fee-other"));
Check("cross-partner private code collision writes nothing", secondPrivate.StatusCode == 503 && Field(secondPrivate, "gate") == CatalogGates.PrivateCodePolicyUnresolved && repo.EntryCount == 2);
Check("code comparison does not invent case folding", Send(http, sa, "POST", "/admin/catalogs/fee_type/entries", "{\"code\":\"Late\",\"display_name\":\"Different case\"}", ("Idempotency-Key", "fee-case")).StatusCode == 201 && repo.EntryCount == 3);

var saList = Send(http, sa, "GET", "/admin/catalogs/fee_type/entries", null);
var paList = Send(http, pa, "GET", "/admin/catalogs/fee_type/entries", null);
var otherList = Send(http, otherPa, "GET", "/admin/catalogs/fee_type/entries", null);
var cashierList = Send(http, cashier, "GET", "/admin/catalogs/fee_type/entries", null);
Check("lists keep both same-code rows for the owner and omit hidden rows without a hidden count",
    saList.StatusCode == 200 && Json(saList).GetProperty("entries").GetArrayLength() == 3
    && saList.Body.Contains(privateId.ToString("D"), StringComparison.Ordinal)
    && paList.StatusCode == 200 && Json(paList).GetProperty("entries").GetArrayLength() == 3
    && otherList.StatusCode == 200 && Json(otherList).GetProperty("entries").GetArrayLength() == 2
    && !otherList.Body.Contains(privateId.ToString("D"), StringComparison.Ordinal)
    && otherList.Body.Contains(globalId.ToString("D"), StringComparison.Ordinal)
    && Json(saList).GetProperty("pagination").GetString() == "not_a_cursor_contract"
    && !Names(Json(saList)).Contains("hidden_count")
    && !saList.Body.Contains("hidden", StringComparison.OrdinalIgnoreCase)
    && cashierList.StatusCode == 403 && Field(cashierList, "code") == "no_management_grant");

var hiddenRead = Send(http, sa, "GET", "/admin/catalogs/fee_type/entries/" + privateId.ToString("D"), null);
var missingRead = Send(http, sa, "GET", "/admin/catalogs/fee_type/entries/" + G(80).ToString("D"), null);
var otherRead = Send(http, otherPa, "GET", "/admin/catalogs/fee_type/entries/" + privateId.ToString("D"), null);
Check("SA can read the private fee and another Partner Admin still matches missing", hiddenRead.StatusCode == 200
    && Json(hiddenRead).GetProperty("entry").GetProperty("code").GetString() == "LATE"
    && otherRead.Raw == missingRead.Raw && missingRead.StatusCode == 404 && missingRead.Body == NotFoundBody());
var ownerRead = Send(http, pa, "GET", "/admin/catalogs/fee_type/entries/" + privateId.ToString("D"), null);
Check("the owner can read the private fee", ownerRead.StatusCode == 200 && Json(ownerRead).GetProperty("entry").GetProperty("code").GetString() == "LATE");
var hiddenHistory = Send(http, sa, "GET", "/admin/catalogs/fee_type/entries/" + privateId.ToString("D") + "/history", null);
var missingHistory = Send(http, sa, "GET", "/admin/catalogs/fee_type/entries/" + G(80).ToString("D") + "/history", null);
Check("SA private fee history is visible", hiddenHistory.StatusCode == 200
    && Json(hiddenHistory).GetProperty("history").GetArrayLength() == 1 && missingHistory.StatusCode == 404);
var globalHistory = Send(http, sa, "GET", "/admin/catalogs/fee_type/entries/" + globalId.ToString("D") + "/history", null);
var ownerHistory = Send(http, pa, "GET", "/admin/catalogs/fee_type/entries/" + privateId.ToString("D") + "/history", null);
var ownerHistoryJson = Json(ownerHistory);
Check("owner history omits the actor and keeps each commit instant", ownerHistory.StatusCode == 200
    && ownerHistoryJson.GetProperty("history").GetArrayLength() == 1
    && Json(globalHistory).GetProperty("history")[0].GetProperty("occurred_at").GetString() == "2026-11-08T19:55:00.0000000Z"
    && ownerHistoryJson.GetProperty("history")[0].GetProperty("occurred_at").GetString() == "2026-11-08T21:55:00.0000000Z"
    && !Names(ownerHistoryJson).Contains("actor_user_id")
    && !ownerHistory.Body.Contains(pa.UserId.ToString("D"), StringComparison.Ordinal)
    && !globalHistory.Body.Contains(sa.UserId.ToString("D"), StringComparison.Ordinal));

var claimId = G(50);
var otherClaimId = G(51);
repo.InstallFixture(claimId, CatalogFamily.ModelHardwareClaim, EntryClass.Private, pa.PartnerId, "claim-a", "Claim A", "seed", 1,
    new Dictionary<string, string>
    {
        ["model_id"] = G(60).ToString("D"),
        ["capability_entry_id"] = G(61).ToString("D"),
        ["option_key"] = "opt",
        ["source_version"] = "v1",
        ["claim_value"] = "old",
        ["availability"] = "listed",
    });
repo.InstallFixture(otherClaimId, CatalogFamily.ModelHardwareClaim, EntryClass.Global, null, "claim-g", "Global claim", null, 4,
    new Dictionary<string, string> { ["claim_value"] = "g", ["availability"] = "listed" });
var comparisonsBefore = repo.VersionComparisons;
var hiddenClaim = Send(http, otherPa, "PATCH", "/admin/catalogs/model_hardware_claim/entries/" + claimId.ToString("D"), "{\"claim_value\":\"guess\"}", ("Idempotency-Key", "hidden-claim"), ("If-Match", "9"));
var missingClaim = Send(http, otherPa, "PATCH", "/admin/catalogs/model_hardware_claim/entries/" + G(81).ToString("D"), "{\"claim_value\":\"guess\"}", ("Idempotency-Key", "missing-claim"), ("If-Match", "9"));
Check("hidden and missing claim edits match before any version comparison", hiddenClaim.Raw == missingClaim.Raw && hiddenClaim.Body == NotFoundBody()
    && repo.VersionComparisons == comparisonsBefore && repo.IntentCount == 3);
var visibleGuess = Send(http, sa, "PATCH", "/admin/catalogs/model_hardware_claim/entries/" + claimId.ToString("D"), "{\"claim_value\":\"guess\"}", ("Idempotency-Key", "visible-zero"), ("If-Match", "0"));
var hiddenZero = Send(http, otherPa, "PATCH", "/admin/catalogs/model_hardware_claim/entries/" + claimId.ToString("D"), "{\"claim_value\":\"guess\"}", ("Idempotency-Key", "hidden-zero"), ("If-Match", "0"));
var missingZero = Send(http, otherPa, "PATCH", "/admin/catalogs/model_hardware_claim/entries/" + G(81).ToString("D"), "{\"claim_value\":\"guess\"}", ("Idempotency-Key", "missing-zero"), ("If-Match", "0"));
Check("a version of zero is the same rejection for visible, hidden, and missing targets", visibleGuess.Raw == hiddenZero.Raw && hiddenZero.Raw == missingZero.Raw
    && Field(visibleGuess, "code") == "version_domain" && repo.VersionComparisons == comparisonsBefore);
Check("a fee id on the claim route matches missing before a version comparison",
    Send(http, pa, "PATCH", "/admin/catalogs/model_hardware_claim/entries/" + privateId.ToString("D"), "{\"claim_value\":\"nope\"}", ("Idempotency-Key", "fee-as-claim"), ("If-Match", "1")).Raw == missingClaim.Raw
    && repo.VersionComparisons == comparisonsBefore);
var forbiddenClaim = Send(http, pa, "PATCH", "/admin/catalogs/model_hardware_claim/entries/" + otherClaimId.ToString("D"), "{\"claim_value\":\"nope\"}", ("Idempotency-Key", "global-claim"), ("If-Match", "1"));
Check("Partner Admin cannot edit a global claim, before version comparison", forbiddenClaim.StatusCode == 403 && Field(forbiddenClaim, "code") == "catalog_claim_edit_actor"
    && repo.VersionComparisons == comparisonsBefore);
Check("a missing If-Match is a precondition failure and does not compare a body version",
    Send(http, sa, "PATCH", "/admin/catalogs/model_hardware_claim/entries/" + claimId.ToString("D"), "{\"expected_version\":1,\"claim_value\":\"next\"}", ("Idempotency-Key", "no-ver")).StatusCode == 428
    && Captured.Response.Raw.StartsWith("HTTP/1.1 428 Precondition Required\r\n", StringComparison.Ordinal)
    && Field(Captured.Response, "code") == "catalog_precondition_required" && repo.VersionComparisons == comparisonsBefore);
Check("ABSENT is not an entry precondition", Send(http, sa, "PATCH", "/admin/catalogs/model_hardware_claim/entries/" + claimId.ToString("D"), "{\"claim_value\":\"next\"}", ("Idempotency-Key", "absent-ver"), ("If-Match", "ABSENT")).StatusCode == 400
    && Field(Captured.Response, "code") == "version_domain" && repo.VersionComparisons == comparisonsBefore);
Check("a contradictory body version is rejected before comparison", Send(http, sa, "PATCH", "/admin/catalogs/model_hardware_claim/entries/" + claimId.ToString("D"), "{\"expected_version\":2,\"claim_value\":\"next\"}", ("Idempotency-Key", "contradict"), ("If-Match", "\"1\"")).StatusCode == 400
    && Field(Captured.Response, "code") == "contradictory_precondition" && repo.VersionComparisons == comparisonsBefore);
Check("a weak or wildcard If-Match is not an exact version", Send(http, sa, "PATCH", "/admin/catalogs/model_hardware_claim/entries/" + claimId.ToString("D"), "{\"claim_value\":\"next\"}", ("Idempotency-Key", "weak"), ("If-Match", "W/\"1\"")).StatusCode == 400
    && Send(http, sa, "PATCH", "/admin/catalogs/model_hardware_claim/entries/" + claimId.ToString("D"), "{\"claim_value\":\"next\"}", ("Idempotency-Key", "star"), ("If-Match", "*")).StatusCode == 400
    && Field(Captured.Response, "code") == "version_domain" && repo.VersionComparisons == comparisonsBefore);
var stale = Send(http, sa, "PATCH", "/admin/catalogs/model_hardware_claim/entries/" + claimId.ToString("D"), "{\"claim_value\":\"next\"}", ("Idempotency-Key", "stale"), ("If-Match", "2"));
Check("a stale claim version conflicts without a write", stale.StatusCode == 409 && Field(stale, "code") == "catalog_version_conflict" && Field(stale, "wrote") == "false"
    && repo.VersionComparisons == comparisonsBefore + 1 && ReadVersion(http, sa, "model_hardware_claim", claimId) == 1);
Check("a protected claim field stays unsettled", Send(http, sa, "PATCH", "/admin/catalogs/model_hardware_claim/entries/" + claimId.ToString("D"), "{\"model_id\":\"" + G(70).ToString("D") + "\"}", ("Idempotency-Key", "protected"), ("If-Match", "1")).StatusCode == 422
    && Captured.Response.Raw.StartsWith("HTTP/1.1 422 Unprocessable Content\r\n", StringComparison.Ordinal)
    && Field(Captured.Response, "code") == "catalog_column_matrix_unsettled" && ReadVersion(http, sa, "model_hardware_claim", claimId) == 1);
Check("an unknown claim field writes nothing", Send(http, sa, "PATCH", "/admin/catalogs/model_hardware_claim/entries/" + claimId.ToString("D"), "{\"supersedes_claim_id\":\"x\"}", ("Idempotency-Key", "bad-claim"), ("If-Match", "1")).StatusCode == 400
    && Field(Captured.Response, "detail") == "supersedes_claim_id" && ReadVersion(http, sa, "model_hardware_claim", claimId) == 1);
Check("claim edit rejects delegation", Send(http, sa, "PATCH", "/admin/catalogs/model_hardware_claim/entries/" + claimId.ToString("D"), "{\"claim_value\":\"next\",\"on_behalf_of\":\"" + G(77).ToString("D") + "\"}", ("Idempotency-Key", "delegated"), ("If-Match", "1")).StatusCode == 400
    && Field(Captured.Response, "code") == "authority_field_rejected" && !Captured.Response.Body.Contains(G(77).ToString("D"), StringComparison.Ordinal) && ReadVersion(http, sa, "model_hardware_claim", claimId) == 1);

var beforeEditIntents = repo.IntentCount;
var edited = Send(http, sa, "PATCH", "/admin/catalogs/model_hardware_claim/entries/" + claimId.ToString("D"), "{\"claim_value\":\"next\",\"checked_at\":\"2026-11-08T12:00:00-07:00\"}", ("Idempotency-Key", "edit-1"), ("If-Match", "\"1\""));
Check("If-Match supplies the entry version and the edit commits the allowlisted fields", edited.StatusCode == 200 && Field(edited, "wrote") == "true" && Field(edited, "entry_version") == "2"
    && Field(edited, "audit_emitted") == "false" && Field(edited, "audit_intent_status") == AuditAllowlist.IntentStatus
    && Json(edited).GetProperty("entry").GetProperty("fields").GetProperty("claim_value").GetString() == "next"
    && Json(edited).GetProperty("entry").GetProperty("fields").GetProperty("checked_at").GetString()!.EndsWith('Z')
    && !Names(Json(edited)).Contains("actor_user_id") && ReadVersion(http, sa, "model_hardware_claim", claimId) == 2 && repo.IntentCount == beforeEditIntents + 1);
var replayEdit = http.Handle(new CatalogHttpServerContext(sa, "edit-other", "edit-req"), Message("PATCH", "/admin/catalogs/model_hardware_claim/entries/" + claimId.ToString("D"), "{\"claim_value\":\"next\",\"checked_at\":\"2026-11-08T12:00:00-07:00\"}", ("Idempotency-Key", "edit-1"), ("If-Match", "\"1\"")));
Check("claim replay returns the same HTTP body and does not append history", replayEdit.Body == edited.Body && repo.IntentCount == beforeEditIntents + 1);
Check("a body version that agrees with If-Match is one precondition", Send(http, sa, "PATCH", "/admin/catalogs/model_hardware_claim/entries/" + claimId.ToString("D"), "{\"expected_version\":2,\"availability\":\"ready\"}", ("Idempotency-Key", "edit-2"), ("If-Match", "2")).StatusCode == 200
    && ReadVersion(http, sa, "model_hardware_claim", claimId) == 3);
Check("a string body version does not agree with a numeric If-Match", Send(http, sa, "PATCH", "/admin/catalogs/model_hardware_claim/entries/" + claimId.ToString("D"), "{\"expected_version\":\"3\",\"claim_value\":\"later\"}", ("Idempotency-Key", "string-ver"), ("If-Match", "3")).StatusCode == 400
    && Field(Captured.Response, "code") == "contradictory_precondition" && ReadVersion(http, sa, "model_hardware_claim", claimId) == 3);
var noop = Send(http, sa, "PATCH", "/admin/catalogs/model_hardware_claim/entries/" + claimId.ToString("D"), "{\"availability\":\"ready\"}", ("Idempotency-Key", "noop"), ("If-Match", "3"));
Check("a same-state claim edit is a no-op, not a replayed success", noop.StatusCode == 409 && Field(noop, "code") == "catalog_noop" && Field(noop, "wrote") == "false"
    && ReadVersion(http, sa, "model_hardware_claim", claimId) == 3 && repo.IntentCount == beforeEditIntents + 2);

var beforeOff = Send(http, sa, "GET", "/admin/catalogs/fee_type/entries/" + globalId.ToString("D"), null);
var offReason = new string('r', 501);
var off = Send(http, sa, "POST", "/admin/catalogs/fee_type/entries/" + globalId.ToString("D") + "/entry-activation", "{\"desired_active\":false,\"reason\":\"" + offReason + "\"}", ("Idempotency-Key", "off"), ("If-Match", "9"));
var afterOff = Send(http, sa, "GET", "/admin/catalogs/fee_type/entries/" + globalId.ToString("D"), null);
Check("a long Off is unavailable, writes nothing, and is not echoed", off.StatusCode == 503 && off.Raw.StartsWith("HTTP/1.1 503 Service Unavailable\r\n", StringComparison.Ordinal)
    && Field(off, "gate") == CatalogGates.GraphUnconfiguredAllOff && Field(off, "wrote") == "false"
    && !off.Body.Contains(offReason, StringComparison.Ordinal) && !Names(Json(off)).Contains("affected_count")
    && beforeOff.Raw == afterOff.Raw && repo.IdempotencyCount == 5);
var shortOff = Send(http, sa, "POST", "/admin/catalogs/fee_type/entries/" + globalId.ToString("D") + "/entry-activation", "{\"desired_active\":false,\"reason\":\"short\"}", ("Idempotency-Key", "off-short"), ("If-Match", "ABSENT"));
var afterShort = Send(http, sa, "GET", "/admin/catalogs/fee_type/entries/" + globalId.ToString("D"), null);
Check("a short Off with ABSENT still writes nothing and stores no replay", shortOff.StatusCode == 503 && Field(shortOff, "wrote") == "false" && afterShort.Raw == beforeOff.Raw
    && repo.IdempotencyCount == 5 && repo.HistoryCount == 5);
Check("a blank reason is refused without a minimum length", Send(http, sa, "POST", "/admin/catalogs/fee_type/entries/" + globalId.ToString("D") + "/entry-activation", "{\"desired_active\":false,\"reason\":\"   \"}", ("Idempotency-Key", "blank"), ("If-Match", "1")).StatusCode == 422
    && Field(Captured.Response, "code") == "catalog_reason_required");
Check("the double input cap is not the owner reason policy", Send(http, sa, "POST", "/admin/catalogs/fee_type/entries/" + globalId.ToString("D") + "/entry-activation", "{\"desired_active\":false,\"reason\":\"" + new string('r', 4001) + "\"}", ("Idempotency-Key", "capped"), ("If-Match", "1")).StatusCode == 400
    && Field(Captured.Response, "code") == "double_input_cap" && Field(Captured.Response, "detail") == "double_input_cap_not_owner_reason_policy" && afterShort.Raw == beforeOff.Raw);
var restore = Send(http, pa, "POST", "/admin/catalogs/fee_type/entries/" + globalId.ToString("D") + "/entry-activation", "{\"desired_active\":true,\"reason\":\"turn it back\"}", ("Idempotency-Key", "on"), ("If-Match", "1"));
Check("graph-dependent restore is unavailable and writes nothing", restore.StatusCode == 503 && Field(restore, "gate") == CatalogGates.GraphDependentRestoreUnavailable && Field(restore, "wrote") == "false"
    && Send(http, sa, "GET", "/admin/catalogs/fee_type/entries/" + globalId.ToString("D"), null).Raw == beforeOff.Raw);

var claimBefore = Send(http, sa, "GET", "/admin/catalogs/model_hardware_claim/entries/" + claimId.ToString("D"), null);
var saClaimOn = Send(http, sa, "POST", "/admin/catalogs/model_hardware_claim/entries/" + claimId.ToString("D") + "/entry-activation", "{\"desired_active\":true,\"reason\":\"reactivate\"}", ("Idempotency-Key", "claim-on-sa"), ("If-Match", "3"));
var paClaimOn = Send(http, pa, "POST", "/admin/catalogs/model_hardware_claim/entries/" + claimId.ToString("D") + "/entry-activation", "{\"desired_active\":true,\"reason\":\"reactivate\"}", ("Idempotency-Key", "claim-on-pa"), ("If-Match", "3"));
Check("claim reactivation is the same unavailable result for SA and the owning PA", saClaimOn.Raw == paClaimOn.Raw && saClaimOn.StatusCode == 503
    && Field(saClaimOn, "gate") == CatalogGates.ClaimReactivationPendingPq272 && Field(saClaimOn, "detail") == "pending_pq272" && Field(saClaimOn, "wrote") == "false"
    && !saClaimOn.Body.Contains("sa_only", StringComparison.OrdinalIgnoreCase) && !saClaimOn.Body.Contains("SA-only", StringComparison.Ordinal)
    && Send(http, sa, "GET", "/admin/catalogs/model_hardware_claim/entries/" + claimId.ToString("D"), null).Raw == claimBefore.Raw);
var otherClaimOn = Send(http, otherPa, "POST", "/admin/catalogs/model_hardware_claim/entries/" + claimId.ToString("D") + "/entry-activation", "{\"desired_active\":true,\"reason\":\"reactivate\"}", ("Idempotency-Key", "claim-on-other"), ("If-Match", "9"));
var missingClaimOn = Send(http, otherPa, "POST", "/admin/catalogs/model_hardware_claim/entries/" + G(81).ToString("D") + "/entry-activation", "{\"desired_active\":true,\"reason\":\"reactivate\"}", ("Idempotency-Key", "claim-on-missing"), ("If-Match", "9"));
Check("a non-owner claim On matches missing before it can reveal a version", otherClaimOn.Raw == missingClaimOn.Raw && otherClaimOn.Body == NotFoundBody());
var claimOff = Send(http, sa, "POST", "/admin/catalogs/model_hardware_claim/entries/" + claimId.ToString("D") + "/entry-activation", "{\"desired_active\":false,\"reason\":\"deactivate\"}", ("Idempotency-Key", "claim-off"), ("If-Match", "3"));
Check("claim Off is blocked because the graph is unknown", claimOff.StatusCode == 503 && Field(claimOff, "gate") == CatalogGates.GraphUnconfiguredAllOff && Field(claimOff, "wrote") == "false"
    && Send(http, sa, "GET", "/admin/catalogs/model_hardware_claim/entries/" + claimId.ToString("D"), null).Raw == claimBefore.Raw);

var hiddenOff = Send(http, otherPa, "POST", "/admin/catalogs/fee_type/entries/" + privateId.ToString("D") + "/entry-activation", "{\"desired_active\":false,\"reason\":\"stop\"}", ("Idempotency-Key", "hidden-off"), ("If-Match", "3"));
var missingOff = Send(http, otherPa, "POST", "/admin/catalogs/fee_type/entries/" + G(80).ToString("D") + "/entry-activation", "{\"desired_active\":false,\"reason\":\"stop\"}", ("Idempotency-Key", "missing-off"), ("If-Match", "3"));
Check("a hidden Off matches a missing Off and is not a version conflict", hiddenOff.Raw == missingOff.Raw && hiddenOff.Body == NotFoundBody() && repo.VersionComparisons == comparisonsBefore + 4);
var cashierOff = Send(http, cashier, "POST", "/admin/catalogs/fee_type/entries/" + globalId.ToString("D") + "/entry-activation", "{\"desired_active\":false,\"reason\":\"stop\"}", ("Idempotency-Key", "cashier-off"), ("If-Match", "1"));
var cashierMissing = Send(http, cashier, "POST", "/admin/catalogs/fee_type/entries/" + G(80).ToString("D") + "/entry-activation", "{\"desired_active\":false,\"reason\":\"stop\"}", ("Idempotency-Key", "cashier-missing"), ("If-Match", "1"));
Check("a cashier Off matches missing instead of revealing the entry", cashierOff.Raw == cashierMissing.Raw && cashierOff.Body == NotFoundBody());

var badPartner = Send(http, sa, "POST", "/admin/catalogs/fee_type/entries/" + globalId.ToString("D") + "/partners/not-a-uuid/activation", "{\"desired_active\":false,\"reason\":\"stop\"}", ("Idempotency-Key", "bad-partner"), ("If-Match", "ABSENT"));
Check("a malformed partner id fails syntax and does not claim existence", badPartner.StatusCode == 400 && Field(badPartner, "code") == "invalid_partner_uuid"
    && !badPartner.Body.Contains("exists", StringComparison.OrdinalIgnoreCase) && !badPartner.Body.Contains("verified", StringComparison.OrdinalIgnoreCase)
    && !Names(Json(badPartner)).Contains("partner_target_check"));
var syntaxPartner = Send(http, sa, "POST", "/admin/catalogs/fee_type/entries/" + globalId.ToString("D") + "/partners/" + G(30).ToString("D") + "/activation", "{\"desired_active\":false,\"reason\":\"stop\"}", ("Idempotency-Key", "syntax-partner"), ("If-Match", "\"ABSENT\""));
Check("a well-formed SA partner id is syntax only and writes nothing", syntaxPartner.StatusCode == 503 && Field(syntaxPartner, "gate") == CatalogGates.GraphUnconfiguredAllOff
    && Field(syntaxPartner, "partner_target_check") == CatalogCommandParser.PartnerTargetSyntaxOnly
    && !syntaxPartner.Body.Contains("verified", StringComparison.OrdinalIgnoreCase) && !syntaxPartner.Body.Contains("exists", StringComparison.OrdinalIgnoreCase)
    && Send(http, sa, "GET", "/admin/catalogs/fee_type/entries/" + globalId.ToString("D"), null).Raw == beforeOff.Raw);
var retarget = Send(http, pa, "POST", "/admin/catalogs/fee_type/entries/" + globalId.ToString("D") + "/partners/" + G(30).ToString("D") + "/activation", "{\"desired_active\":false,\"reason\":\"stop\"}", ("Idempotency-Key", "retarget"), ("If-Match", "ABSENT"));
Check("Partner Admin cannot retarget the partner", retarget.StatusCode == 400 && Field(retarget, "code") == "contradictory_partner" && repo.EntryCount == 5);
var privateSwitch = Send(http, pa, "POST", "/admin/catalogs/fee_type/entries/" + privateId.ToString("D") + "/partners/" + pa.PartnerId!.Value.ToString("D") + "/activation", "{\"desired_active\":false,\"reason\":\"stop\"}", ("Idempotency-Key", "private-switch"), ("If-Match", "ABSENT"));
Check("private partner-switch representation stays unresolved", privateSwitch.StatusCode == 503 && Field(privateSwitch, "gate") == CatalogGates.PrivatePartnerSwitchUnresolved
    && Field(privateSwitch, "detail") == OpenPolicies.PrivatePartnerSwitch && Field(privateSwitch, "wrote") == "false");
var hiddenLocation = Send(http, sa, "POST", "/admin/catalogs/fee_type/entries/" + privateId.ToString("D") + "/locations/" + G(40).ToString("D") + "/activation", "{\"desired_active\":false,\"reason\":\"stop\"}", ("Idempotency-Key", "hidden-loc"), ("If-Match", "1"));
var missingLocation = Send(http, sa, "POST", "/admin/catalogs/fee_type/entries/" + G(80).ToString("D") + "/locations/" + G(40).ToString("D") + "/activation", "{\"desired_active\":false,\"reason\":\"stop\"}", ("Idempotency-Key", "missing-loc"), ("If-Match", "1"));
Check("SA location switch on a visible private fee stays the held denial", hiddenLocation.StatusCode == 403
    && Field(hiddenLocation, "code") == "sa_has_no_location_switch" && Field(hiddenLocation, "wrote") == "false"
    && missingLocation.StatusCode == 404);
var saLocation = Send(http, sa, "POST", "/admin/catalogs/fee_type/entries/" + globalId.ToString("D") + "/locations/" + G(40).ToString("D") + "/activation", "{\"desired_active\":false,\"reason\":\"stop\"}", ("Idempotency-Key", "sa-loc"), ("If-Match", "ABSENT"));
Check("SA has no location switch", saLocation.StatusCode == 403 && Field(saLocation, "code") == "sa_has_no_location_switch" && Field(saLocation, "wrote") == "false");
var paLocation = Send(http, pa, "POST", "/admin/catalogs/fee_type/entries/" + globalId.ToString("D") + "/locations/" + G(40).ToString("D") + "/activation", "{\"desired_active\":true,\"reason\":\"resume\"}", ("Idempotency-Key", "pa-loc"), ("If-Match", "ABSENT"));
Check("location membership is not verified and restore stays unavailable", paLocation.StatusCode == 503 && Field(paLocation, "gate") == CatalogGates.GraphDependentRestoreUnavailable
    && Field(paLocation, "location_membership") == "not_verified" && Field(paLocation, "wrote") == "false");
Check("a malformed location id is rejected", Send(http, pa, "POST", "/admin/catalogs/fee_type/entries/" + globalId.ToString("D") + "/locations/loc/activation", "{\"desired_active\":false,\"reason\":\"stop\"}", ("Idempotency-Key", "bad-loc"), ("If-Match", "ABSENT")).StatusCode == 400
    && Field(Captured.Response, "code") == "invalid_location_uuid");

var previewCounts = repo.HistoryCount;
var missingPreview = http.Handle(new CatalogHttpServerContext(sa, "c", "r", GraphView.Default), Message("POST", "/admin/catalogs/fee_type/entries/" + G(82).ToString("D") + "/activation-preview", "{}"));
var hiddenPreview = http.Handle(new CatalogHttpServerContext(sa, "c", "r", new GraphView.Fixture("rev-empty", [])), Message("POST", "/admin/catalogs/fee_type/entries/" + privateId.ToString("D") + "/activation-preview", "{}"));
Check("SA preview of a visible private fee stays a held fixture preview", hiddenPreview.StatusCode == 200 && Field(hiddenPreview, "wrote") == "false"
    && Json(hiddenPreview).GetProperty("preview").GetProperty("impact").GetString() == "fixture_not_authorization"
    && Json(hiddenPreview).GetProperty("preview").GetProperty("authorizes_writes").GetBoolean() == false
    && Json(hiddenPreview).GetProperty("preview").GetProperty("apply_allowed").GetBoolean() == false
    && missingPreview.StatusCode == 404 && missingPreview.Body == NotFoundBody());
var unknownPreview = Send(http, sa, "POST", "/admin/catalogs/fee_type/entries/" + globalId.ToString("D") + "/activation-preview", null);
var unknownPreviewObject = Json(unknownPreview).GetProperty("preview");
Check("UNCONFIGURED preview impact is unknown and has no count", unknownPreview.StatusCode == 200 && Field(unknownPreview, "wrote") == "false"
    && unknownPreviewObject.GetProperty("impact").GetString() == "unknown" && unknownPreviewObject.GetProperty("apply_allowed").GetBoolean() == false
    && unknownPreviewObject.GetProperty("runtime_configuration").GetBoolean() == false && unknownPreviewObject.GetProperty("authorizes_writes").GetBoolean() == false
    && !unknownPreviewObject.TryGetProperty("affected_count", out _) && !unknownPreviewObject.TryGetProperty("fixture_declared_edge_count", out _)
    && repo.HistoryCount == previewCounts);
var edgedPreview = http.Handle(new CatalogHttpServerContext(sa, "c", "r", new GraphView.Fixture("rev-edge", [new FixtureEdge(globalId, claimId)])),
    Message("POST", "/admin/catalogs/fee_type/entries/" + globalId.ToString("D") + "/activation-preview", ""));
var edgedObject = Json(edgedPreview).GetProperty("preview");
Check("a fixture edge is not an impact count and does not authorize", edgedPreview.StatusCode == 200
    && edgedObject.GetProperty("impact").GetString() == "fixture_not_authorization" && edgedObject.GetProperty("fixture_declared_edge_count").GetInt32() == 1
    && !edgedObject.TryGetProperty("affected_count", out _) && edgedObject.GetProperty("authorizes_writes").GetBoolean() == false
    && edgedObject.GetProperty("runtime_configuration").GetBoolean() == false && ReadVersion(http, sa, "model_hardware_claim", claimId) == 3);
var emptyFixture = http.Handle(new CatalogHttpServerContext(sa, "c", "r", new GraphView.Fixture("rev-empty", [])),
    Message("POST", "/admin/catalogs/fee_type/entries/" + globalId.ToString("D") + "/activation-preview", "{}"));
Check("an empty fixture is not a zero-impact leaf", Json(emptyFixture).GetProperty("preview").GetProperty("fixture_declared_edge_count").GetInt32() == 0
    && !Json(emptyFixture).GetProperty("preview").TryGetProperty("affected_count", out _) && Field(emptyFixture, "wrote") == "false");
var previewActor = Send(http, sa, "POST", "/admin/catalogs/fee_type/entries/" + globalId.ToString("D") + "/activation-preview", "{\"actor_user_id\":\"" + G(99).ToString("D") + "\"}");
var previewEdge = Send(http, sa, "POST", "/admin/catalogs/fee_type/entries/" + globalId.ToString("D") + "/activation-preview", "{\"edges\":1,\"desired_active\":false}");
Check("preview rejects actor and graph fields from the body", previewActor.StatusCode == 400 && Field(previewActor, "code") == "authority_field_rejected"
    && previewEdge.StatusCode == 400 && Field(previewEdge, "code") == "unknown_field" && repo.HistoryCount == previewCounts);
var fixtureOff = http.Handle(new CatalogHttpServerContext(sa, "c", "r", new GraphView.Fixture("rev-edge", [new FixtureEdge(globalId, claimId)])),
    Message("POST", "/admin/catalogs/fee_type/entries/" + globalId.ToString("D") + "/entry-activation", "{\"desired_active\":false,\"reason\":\"stop\"}", ("Idempotency-Key", "fixture-off"), ("If-Match", "1")));
Check("a fixture graph does not make Off available", fixtureOff.StatusCode == 503 && Field(fixtureOff, "gate") == CatalogGates.GraphUnconfiguredAllOff
    && Send(http, sa, "GET", "/admin/catalogs/fee_type/entries/" + globalId.ToString("D"), null).Raw == beforeOff.Raw);

foreach (var family in CatalogFamilies.All)
{
    if (family is CatalogFamily.FeeType or CatalogFamily.ModelHardwareClaim)
        continue;
    var fixtureId = Guid.NewGuid();
    repo.InstallFixture(fixtureId, family, EntryClass.Global, null, "code-" + CatalogFamilies.Wire(family), "Name", null, 1, null);
    var seen = Send(http, sa, "GET", "/admin/catalogs/" + CatalogFamilies.Wire(family) + "/entries/" + fixtureId.ToString("D"), null);
    var familyOff = Send(http, sa, "POST", "/admin/catalogs/" + CatalogFamilies.Wire(family) + "/entries/" + fixtureId.ToString("D") + "/entry-activation",
        "{\"desired_active\":false,\"reason\":\"stop this family\"}", ("Idempotency-Key", "off-" + CatalogFamilies.Wire(family)), ("If-Match", "1"));
    var seenAfter = Send(http, sa, "GET", "/admin/catalogs/" + CatalogFamilies.Wire(family) + "/entries/" + fixtureId.ToString("D"), null);
    Check($"{CatalogFamilies.Wire(family)} Off writes nothing while the graph is unknown", familyOff.StatusCode == 503 && Field(familyOff, "gate") == CatalogGates.GraphUnconfiguredAllOff
        && Field(familyOff, "wrote") == "false" && seen.Raw == seenAfter.Raw && !Names(Json(familyOff)).Contains("affected_count"));
}

var pan = "4111111111111111";
var sensitive = Send(http, sa, "POST", "/admin/catalogs/fee_type/entries", "{\"code\":\"panfee\",\"display_name\":\"" + pan + "\"}", ("Idempotency-Key", "pan"));
Check("a card-shaped value is rejected without being echoed", sensitive.StatusCode == 400 && Field(sensitive, "code") == "sensitive_value_rejected" && !sensitive.Body.Contains(pan, StringComparison.Ordinal) && !sensitive.Raw.Contains(pan, StringComparison.Ordinal));
Check("duplicate JSON keys are rejected", Send(http, sa, "POST", "/admin/catalogs/fee_type/entries", "{\"code\":\"a\",\"code\":\"b\",\"display_name\":\"A\"}", ("Idempotency-Key", "dup")).StatusCode == 400
    && Field(Captured.Response, "code") == "invalid_dto" && Field(Captured.Response, "detail") == "duplicate_field");

var faultRepo = new InMemoryCatalogRepository { CommitFault = new InvalidOperationException("injected") };
var faultHttp = new CatalogHttpTransport(new CatalogDomainService(faultRepo, clock, new CountingIds()));
var rolled = Send(faultHttp, sa, "POST", "/admin/catalogs/fee_type/entries", "{\"code\":\"rollback\",\"display_name\":\"Rollback\"}", ("Idempotency-Key", "rollback"));
Check("a failed commit leaves no residue and is not a success replay", rolled.StatusCode == 503 && Field(rolled, "code") == "commit_rolled_back" && Field(rolled, "wrote") == "false"
    && !rolled.Body.Contains("injected", StringComparison.Ordinal) && !rolled.Body.Contains(FrozenAuditSchemaBoundary.DurableAcceptedNotClaimed, StringComparison.Ordinal)
    && faultRepo.EntryCount == 0 && faultRepo.HistoryCount == 0 && faultRepo.IntentCount == 0 && faultRepo.IdempotencyCount == 0);
faultRepo.CommitFault = null;
var retried = Send(faultHttp, sa, "POST", "/admin/catalogs/fee_type/entries", "{\"code\":\"rollback\",\"display_name\":\"Rollback\"}", ("Idempotency-Key", "rollback"));
Check("the same key can commit after rollback", retried.StatusCode == 201 && faultRepo.EntryCount == 1 && faultRepo.HistoryCount == 1 && faultRepo.IntentCount == 1);

var parallelRepo = new InMemoryCatalogRepository();
var parallelHttp = new CatalogHttpTransport(new CatalogDomainService(parallelRepo, clock, new CountingIds()));
var entered = new ManualResetEventSlim(false);
var release = new ManualResetEventSlim(false);
var commitEntries = 0;
parallelRepo.BeforeCommit = () =>
{
    Interlocked.Increment(ref commitEntries);
    entered.Set();
    release.Wait();
};
var parallelMessage = Message("POST", "/admin/catalogs/fee_type/entries", "{\"code\":\"parallel\",\"display_name\":\"Parallel\"}", ("Idempotency-Key", "parallel"));
var parallelContext = Ctx(sa);
var first = Task.Run(() => parallelHttp.Handle(parallelContext, parallelMessage));
var second = Task.Run(() => parallelHttp.Handle(parallelContext, parallelMessage));
Check("parallel same key reaches the commit once", entered.Wait(TimeSpan.FromSeconds(5)) && parallelRepo.HistoryCount == 0);
release.Set();
var finished = Task.WaitAll([first, second], TimeSpan.FromSeconds(5));
Check("parallel same key commits one HTTP result", finished && commitEntries == 1 && first.Result.StatusCode == 201 && first.Result.Body == second.Result.Body
    && parallelRepo.HistoryCount == 1 && parallelRepo.IntentCount == 1 && parallelRepo.CommitAttempts == 1);

Console.WriteLine($"Catalog.Transport checks: {pass} passed, {fail.Count} failed " + (fail.Count == 0 ? "ALL PASS" : "FAILED: " + string.Join("; ", fail)));
return fail.Count == 0 ? 0 : 1;

static string NotFoundBody() =>
    "{\"code\":\"not_found\",\"detail\":\"not_found\",\"wrote\":false,\"proof_boundary\":\"" + ProofBoundaryNames.InMemoryDouble + "\",\"mapping\":\"" + CatalogHttpMapping.Name + "\"}";

static CatalogHttpServerContext Ctx(CatalogPrincipal principal) => new(principal, "corr", "req");

static SyntheticHttpResponse Send(CatalogHttpTransport transport, CatalogPrincipal principal, string method, string target, string? body, params (string Name, string Value)[] headers) =>
    Captured.Response = transport.Handle(Ctx(principal), Message(method, target, body, headers));

static bool Refusal(CatalogHttpTransport transport, CatalogHttpServerContext context, string raw, string code)
{
    Captured.Response = transport.Handle(context, raw);
    return Captured.Response.StatusCode == 400 && Field(Captured.Response, "code") == code && Field(Captured.Response, "wrote") == "false";
}

static string Field(SyntheticHttpResponse response, string name)
{
    using var document = JsonDocument.Parse(response.Body);
    if (!document.RootElement.TryGetProperty(name, out var value))
        return "";
    return value.ValueKind switch
    {
        JsonValueKind.String => value.GetString() ?? "",
        JsonValueKind.True => "true",
        JsonValueKind.False => "false",
        _ => value.GetRawText(),
    };
}

static JsonElement Json(SyntheticHttpResponse response)
{
    using var document = JsonDocument.Parse(response.Body);
    return document.RootElement.Clone();
}

static HashSet<string> Names(JsonElement element)
{
    var names = new HashSet<string>(StringComparer.Ordinal);
    Walk(element, names);
    return names;
}

static void Walk(JsonElement element, HashSet<string> names)
{
    switch (element.ValueKind)
    {
        case JsonValueKind.Object:
            foreach (var property in element.EnumerateObject())
            {
                names.Add(property.Name);
                Walk(property.Value, names);
            }
            break;
        case JsonValueKind.Array:
            foreach (var item in element.EnumerateArray())
                Walk(item, names);
            break;
    }
}

static long ReadVersion(CatalogHttpTransport transport, CatalogPrincipal principal, string family, Guid entryId)
{
    var response = transport.Handle(Ctx(principal), Message("GET", "/admin/catalogs/" + family + "/entries/" + entryId.ToString("D"), null));
    using var document = JsonDocument.Parse(response.Body);
    return document.RootElement.GetProperty("entry").GetProperty("entry_version").GetInt64();
}

static string Message(string method, string target, string? body, params (string Name, string Value)[] headers)
{
    var builder = new StringBuilder();
    builder.Append(method).Append(' ').Append(target).Append(" HTTP/1.1\r\n");
    builder.Append("Host: catalog.test\r\n");
    foreach (var header in headers)
        builder.Append(header.Name).Append(": ").Append(header.Value).Append("\r\n");
    if (body is not null)
    {
        builder.Append("Content-Type: application/json\r\n");
        builder.Append("Content-Length: ").Append(Encoding.UTF8.GetByteCount(body).ToString()).Append("\r\n");
        builder.Append("\r\n");
        builder.Append(body);
    }
    else
    {
        builder.Append("\r\n");
    }
    return builder.ToString();
}

static string Sha256(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

static Guid G(int n)
{
    var bytes = new byte[16];
    bytes[15] = (byte)n;
    bytes[0] = 0x5f;
    return new Guid(bytes);
}

static string RepoRoot()
{
    var dir = new DirectoryInfo(AppContext.BaseDirectory);
    while (dir is not null)
    {
        if (File.Exists(Path.Combine(dir.FullName, "contract", "audit-events.schema.json"))
            && File.Exists(Path.Combine(dir.FullName, "src", "Catalog.Transport", "Catalog.Transport.csproj")))
            return dir.FullName;
        dir = dir.Parent;
    }
    throw new InvalidOperationException("Repository root was not found from the check output directory.");
}

static class Captured
{
    public static SyntheticHttpResponse Response { get; set; } = new()
    {
        StatusCode = 0,
        ReasonPhrase = "",
        Body = "",
        Raw = "",
        Headers = new Dictionary<string, string>(),
    };
}

sealed class ManualClock(DateTimeOffset start) : ICatalogClock
{
    public DateTimeOffset UtcNow { get; set; } = start;
}

sealed class CountingIds : ICatalogIds
{
    private int _n;

    public Guid NewId()
    {
        lock (this)
        {
            _n++;
            var bytes = new byte[16];
            BitConverter.TryWriteBytes(bytes, _n);
            bytes[15] = 0x5c;
            return new Guid(bytes);
        }
    }
}
