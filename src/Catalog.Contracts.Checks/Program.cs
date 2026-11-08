using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Catalog.Contracts;
using Catalog.Contracts.Doubles;

Console.WriteLine("Catalog.Contracts.Checks: in-memory doubles only. Not DB, runtime, relay, or acceptance.");

var pass = 0;
var fail = new List<string>();
void Check(string name, bool ok)
{
    Console.WriteLine((ok ? "PASS " : "FAIL ") + name);
    if (ok) pass++;
    else fail.Add(name);
}

var root = RepoRoot();
var schemaPath = Path.Combine(root, "contract", "audit-events.schema.json");
var errorsPath = Path.Combine(root, "contract", "errors.md");
var schemaBytes = File.ReadAllBytes(schemaPath);
var errorsBytes = File.ReadAllBytes(errorsPath);
Check("frozen audit schema hash", Sha256(schemaBytes) == "ce0b904fcc5069d9e727e62600efabca311c8a407297ed5dbc96f605ae807b1b" && schemaBytes.Length == 39962);
Check("frozen errors.md hash", Sha256(errorsBytes) == "dd15977e50be88364fa758c8c8c32618539ddfaed43e31205e4b9b65617cd80b" && errorsBytes.Length == 8473);
var schemaText = Encoding.UTF8.GetString(schemaBytes);
Check("frozen schema still requires numeric schema_version and auth event names",
    schemaText.Contains("\"schema_version\": { \"type\": \"string\", \"pattern\": \"^\\\\d+\\\\.\\\\d+\\\\.\\\\d+$\" }", StringComparison.Ordinal)
    && schemaText.Contains("\"pattern\": \"^auth\\\\.[a-z_]+\\\\.[a-z_]+$\"", StringComparison.Ordinal));
Check("unapproved sentinel does not match the frozen numeric pattern", !System.Text.RegularExpressions.Regex.IsMatch(FrozenAuditSchemaBoundary.RejectedSentinel, "^\\d+\\.\\d+\\.\\d+$"));
Check("catalog event name is not an auth source event", !System.Text.RegularExpressions.Regex.IsMatch(ProposedCatalogEvents.Created, "^auth\\.[a-z_]+\\.[a-z_]+$"));

var library = Path.Combine(root, "src", "Catalog.Contracts");
var sources = Directory.GetFiles(library, "*.cs", SearchOption.AllDirectories);
var sourceText = string.Join("\n", sources.Select(File.ReadAllText));
Check("sentinel literal is only the rejection constant", Count(sourceText, FrozenAuditSchemaBoundary.RejectedSentinel) == 1);
Check("DURABLY_ACCEPTED literal is only the rejection constant", Count(sourceText, FrozenAuditSchemaBoundary.DurableAcceptedNotClaimed) == 1);
Check("library does not reference a database, host, or auth outbox",
    !sourceText.Contains("Npgsql", StringComparison.Ordinal)
    && !sourceText.Contains("Auth.Data", StringComparison.Ordinal)
    && !sourceText.Contains("Admin.Api", StringComparison.Ordinal)
    && !sourceText.Contains("DbContext", StringComparison.Ordinal)
    && !sourceText.Contains("audit_outbox", StringComparison.Ordinal)
    && !sourceText.Contains("Migration", StringComparison.Ordinal));
var libraryProject = File.ReadAllText(Path.Combine(library, "Catalog.Contracts.csproj"));
var checksProject = File.ReadAllText(Path.Combine(root, "src", "Catalog.Contracts.Checks", "Catalog.Contracts.Checks.csproj"));
Check("library has no project or package reference", !libraryProject.Contains("ProjectReference", StringComparison.Ordinal) && !libraryProject.Contains("PackageReference", StringComparison.Ordinal));
Check("checks reference only the catalog library", checksProject.Contains("Catalog.Contracts.csproj", StringComparison.Ordinal) && Count(checksProject, "ProjectReference") == 1 && !checksProject.Contains("PackageReference", StringComparison.Ordinal));
Check("forms SA audit gap is open and not waived", FormsSaAuditGap.Disposition == "open_not_fixed_not_waived");

Check("exactly twenty families", CatalogFamilies.All.Count == 20 && CatalogFamilies.All.Distinct().Count() == 20);
foreach (var family in CatalogFamilies.All)
    Check($"wire name parses {CatalogFamilies.Wire(family)}", CatalogFamilies.TryParse(CatalogFamilies.Wire(family), out var parsed) && parsed == family);
Check("unknown family does not parse", !CatalogFamilies.TryParse("widget", out _) && !CatalogFamilies.TryParse("Fee_Type", out _));
Check("six Partner Admin add families", CatalogFamilies.All.Count(CatalogFamilies.IsPartnerAdminAddFamily) == 6
    && CatalogFamilies.IsPartnerAdminAddFamily(CatalogFamily.IdDocumentType)
    && CatalogFamilies.IsPartnerAdminAddFamily(CatalogFamily.FeeType)
    && !CatalogFamilies.IsPartnerAdminAddFamily(CatalogFamily.ModelHardwareClaim));
Check("release, id document, and currency creates are the unresolved required-field set",
    CatalogFamilies.Disposition(CatalogFamily.CatalogRelease) == CreateFieldDisposition.UnresolvedRequiredFields
    && CatalogFamilies.Disposition(CatalogFamily.IdDocumentType) == CreateFieldDisposition.UnresolvedRequiredFields
    && CatalogFamilies.Disposition(CatalogFamily.Currency) == CreateFieldDisposition.UnresolvedRequiredFields
    && CatalogFamilies.Disposition(CatalogFamily.FeeType) == CreateFieldDisposition.NamesOnlyFee
    && CatalogFamilies.Disposition(CatalogFamily.Manufacturer) == CreateFieldDisposition.FieldsNotClosed);
Check("default role has no grant", default(CatalogRole) == CatalogRole.None);
Check("Partner Admin without a partner is rejected", Throws<ArgumentException>(() => new CatalogPrincipal(Guid.NewGuid(), CatalogRole.PartnerAdmin, null)));
Check("Super Admin cannot carry a partner id", Throws<ArgumentException>(() => new CatalogPrincipal(Guid.NewGuid(), CatalogRole.SuperAdmin, Guid.NewGuid())));
Check("ABSENT is not version zero", VersionToken.Absent.IsAbsent && VersionToken.Absent.Number is null && VersionToken.Absent.ToString() == "ABSENT");
Check("version zero is not a token", !VersionToken.TryParseJson(Json("0"), out _, out var zeroError) && zeroError == "version_domain");
Check("version domain rejects a negative", !VersionToken.TryParseJson(Json("-1"), out _, out _));
Check("ABSENT text parses", VersionToken.TryParseJson(Json("\"ABSENT\""), out var absent, out _) && absent.IsAbsent);

var table = new ScopeVersionTable();
table.InsertFromAbsent("partner");
Check("ABSENT insert stores version 1", table.Read("partner").Number == 1 && table.Read("missing").IsAbsent && !table.ContainsZero);
Check("second ABSENT insert conflicts and stays at 1", Throws<ScopeVersionConflictException>(() => table.InsertFromAbsent("partner")) && table.Read("partner").Number == 1);
Check("expected zero is a version-domain failure", Throws<ScopeVersionDomainException>(() => table.UpdateExisting("partner", 0)));
Check("entry version does not create a missing switch", Throws<ScopeVersionConflictException>(() => table.UpdateExisting("location", 1)) && table.Read("location").IsAbsent && table.Count == 1);
table.UpdateExisting("partner", 1);
Check("matching switch version increments by one", table.Read("partner").Number == 2 && !table.ContainsZero);

var ids = new CountingIds();
var clock = new ManualClock(DateTimeOffset.Parse("2026-11-08T19:55:00Z"));
var repo = new InMemoryCatalogRepository();
var service = new CatalogDomainService(repo, clock, ids);
var sa = new CatalogPrincipal(G(1), CatalogRole.SuperAdmin, null);
var pa = new CatalogPrincipal(G(2), CatalogRole.PartnerAdmin, G(20));
var otherPa = new CatalogPrincipal(G(3), CatalogRole.PartnerAdmin, G(21));
var cashier = new CatalogPrincipal(G(4), CatalogRole.Cashier, null);

foreach (var family in new[] { CatalogFamily.CatalogRelease, CatalogFamily.IdDocumentType, CatalogFamily.Currency })
{
    var created = service.Execute(sa, Call("ignored"), CatalogCommandParser.ParseCreate(CatalogFamilies.Wire(family), "{\"code\":\"x\",\"display_name\":\"X\"}"));
    Check($"{CatalogFamilies.Wire(family)} create stays unavailable", created.Status == ProposedStatus.Unavailable && created.Gate == CatalogGates.CreateDtoUnresolved && created.Detail == "required_fields_unresolved" && !created.Wrote);
}
var manufacturer = service.Execute(pa, Call("ignored"), CatalogCommandParser.ParseCreate("manufacturer", "{\"code\":\"acme\",\"display_name\":\"Acme\"}"));
Check("manufacturer create fields stay unclosed", manufacturer.Status == ProposedStatus.Unavailable && manufacturer.Detail == "create_fields_not_closed" && !manufacturer.Wrote && repo.EntryCount == 0);
var impersonated = service.Execute(pa, Call("imp"), CatalogCommandParser.ParseCreate("fee_type", "{\"code\":\"late\",\"display_name\":\"Late\",\"owner_partner_id\":\"" + G(99) + "\"}"));
Check("create body cannot choose an owner", impersonated.Status == ProposedStatus.BadRequest && impersonated.Code == "authority_field_rejected" && repo.EntryCount == 0);
var classified = service.Execute(pa, Call("fee-class"), CatalogCommandParser.ParseCreate("fee_type", "{\"code\":\"late\",\"display_name\":\"Late\",\"economic_role\":\"unknown\"}"));
Check("fee economic_role is an unknown field and stores no default", classified.Status == ProposedStatus.BadRequest && classified.Code == "unknown_field" && classified.Detail == "economic_role"
    && !classified.Detail!.Contains("default", StringComparison.OrdinalIgnoreCase) && repo.EntryCount == 0 && repo.IntentCount == 0);
var sentinelBody = service.Execute(sa, Call("sent"), CatalogCommandParser.ParseCreate("fee_type", "{\"code\":\"late\",\"display_name\":\"" + FrozenAuditSchemaBoundary.RejectedSentinel + "\"}"));
Check("sentinel value is rejected and not stored", sentinelBody.Code == "schema_version_rejected" && repo.EntryCount == 0);
var sentinelKey = service.Execute(sa, Call("sent-key"), CatalogCommandParser.ParseCreate("fee_type", "{\"code\":\"late\",\"display_name\":\"Late\",\"schema_version\":\"1.2.0\"}"));
Check("schema_version field is rejected even when the value is numeric", sentinelKey.Code == "authority_field_rejected" && repo.EntryCount == 0);

var missingKey = service.Execute(sa, new CatalogCall(null, "c", "r"), CatalogCommandParser.ParseCreate("fee_type", "{\"code\":\"late\",\"display_name\":\"Late\"}"));
Check("write requires an idempotency key", missingKey.Code == "idempotency_key_required" && repo.EntryCount == 0);
var cashierCreate = service.Execute(cashier, Call("cashier"), CatalogCommandParser.ParseCreate("fee_type", "{\"code\":\"late\",\"display_name\":\"Late\"}"));
Check("cashier cannot create", cashierCreate.Status == ProposedStatus.Forbidden && cashierCreate.Code == "no_management_grant" && repo.EntryCount == 0);

var takenBefore = ids.Taken;
var createdGlobal = service.Execute(sa, Call("fee-global", "corr-1", "req-1"), CatalogCommandParser.ParseCreate("fee_type", "{\"code\":\"LATE\",\"display_name\":\"Late fee\"}"));
Check("SA fee create is global names only", createdGlobal.Status == ProposedStatus.Created && createdGlobal.Wrote && createdGlobal.EntryVersion == 1
    && createdGlobal.Entry!.EntryClass == "global" && createdGlobal.Entry.OwnerPartnerId is null
    && createdGlobal.Entry.Fields.Count == 0 && !createdGlobal.Entry.Fields.ContainsKey("economic_role")
    && createdGlobal.Entry.EffectiveAvailability == "unknown"
    && createdGlobal.Entry.EntryActivation.Token == "ABSENT" && createdGlobal.Entry.EntryActivation.Number is null && createdGlobal.Entry.EntryActivation.Active is null
    && createdGlobal.AuditIntentStatus == AuditAllowlist.IntentStatus
    && createdGlobal.ProofBoundary == ProofBoundaryNames.InMemoryDouble
    && createdGlobal.Intent!.ProposedEventName == ProposedCatalogEvents.Created
    && createdGlobal.Intent.Fields["emitted"] == "false"
    && createdGlobal.Intent.Fields["actor_user_id"] == sa.UserId.ToString("D")
    && createdGlobal.Intent.After.Keys.All(AuditAllowlist.FeeValueKeys.Contains)
    && !createdGlobal.Intent.After.ContainsKey("economic_role"));
var globalId = createdGlobal.EntryId!.Value;
clock.UtcNow = clock.UtcNow.AddHours(2);
var replay = service.Execute(sa, Call("fee-global", "corr-2", "req-2"), CatalogCommandParser.ParseCreate("fee_type", "{\"code\":\"LATE\",\"display_name\":\"Late fee\"}"));
Check("same key replays the committed result without a second history row", replay.EntryId == globalId && replay.Intent!.EventId == createdGlobal.Intent!.EventId
    && replay.Intent.Fields["occurred_at"] == createdGlobal.Intent.Fields["occurred_at"] && repo.HistoryCount == 1 && repo.IntentCount == 1 && ids.Taken == takenBefore + 3);
var conflict = service.Execute(sa, Call("fee-global"), CatalogCommandParser.ParseCreate("fee_type", "{\"code\":\"OTHER\",\"display_name\":\"Other\"}"));
Check("same key with a different body writes nothing", conflict.Status == ProposedStatus.Conflict && conflict.Code == "idempotency_conflict" && !conflict.Wrote && repo.EntryCount == 1);
var otherActor = service.Execute(pa, Call("fee-global"), CatalogCommandParser.ParseCreate("fee_type", "{\"code\":\"LATE\",\"display_name\":\"Late fee\"}"));
Check("another actor cannot reuse the key as a replay", otherActor.Code == "idempotency_conflict" && repo.EntryCount == 1);
var privateFee = service.Execute(pa, Call("fee-private"), CatalogCommandParser.ParseCreate("fee_type", "{\"code\":\"LATE\",\"display_name\":\"Late fee\",\"description\":\"Name only\"}"));
Check("PA fee create is private and may share the global code", privateFee.Status == ProposedStatus.Created && privateFee.Entry!.EntryClass == "private"
    && privateFee.Entry.OwnerPartnerId == pa.PartnerId && privateFee.EntryId != globalId && privateFee.Intent!.After.ContainsKey("description"));
var secondGlobal = service.Execute(sa, Call("fee-global-2"), CatalogCommandParser.ParseCreate("fee_type", "{\"code\":\"LATE\",\"display_name\":\"Late again\"}"));
Check("a second global with the same code is an unresolved policy, not a uniqueness claim", secondGlobal.Status == ProposedStatus.Unavailable && secondGlobal.Detail == "code_policy_unresolved"
    && !secondGlobal.Detail.Contains("unique", StringComparison.OrdinalIgnoreCase) && repo.EntryCount == 2);
var secondPrivate = service.Execute(otherPa, Call("fee-other"), CatalogCommandParser.ParseCreate("fee_type", "{\"code\":\"LATE\",\"display_name\":\"Other private\"}"));
Check("cross-partner private code collision writes nothing", secondPrivate.Gate == CatalogGates.PrivateCodePolicyUnresolved && secondPrivate.Detail == "code_policy_unresolved" && repo.EntryCount == 2);
var differentCase = service.Execute(sa, Call("fee-case"), CatalogCommandParser.ParseCreate("fee_type", "{\"code\":\"Late\",\"display_name\":\"Different case\"}"));
Check("code comparison does not invent case folding", differentCase.Status == ProposedStatus.Created && repo.EntryCount == 3);

var saList = service.List(sa, CatalogFamily.FeeType);
var paList = service.List(pa, CatalogFamily.FeeType);
var otherList = service.List(otherPa, CatalogFamily.FeeType);
var cashierList = service.List(cashier, CatalogFamily.FeeType);
Check("lists keep both same-code rows for the owner and omit hidden rows without a hidden count",
    saList.List!.Entries.Count == 2
    && paList.List!.Entries.Count == 3
    && otherList.List!.Entries.Count == 2
    && otherList.List.Entries.Count(entry => entry.EntryId == privateFee.EntryId) == 0
    && otherList.List.Entries.Any(entry => entry.EntryId == globalId)
    && cashierList.Status == ProposedStatus.Forbidden
    && typeof(EntryList).GetProperties().All(property => property.Name != "HiddenCount")
    && saList.List.Pagination == "not_a_cursor_contract");
var hiddenRead = service.Read(sa, CatalogFamily.FeeType, privateFee.EntryId!.Value);
var missingRead = service.Read(sa, CatalogFamily.FeeType, G(80));
Check("SA private non-claim read matches missing", OutcomeCompare.SameRefusal(hiddenRead, missingRead) && hiddenRead.Status == ProposedStatus.NotFound);
var otherRead = service.Read(otherPa, CatalogFamily.FeeType, privateFee.EntryId!.Value);
Check("other Partner Admin private read matches missing", OutcomeCompare.SameRefusal(otherRead, missingRead));
var ownerRead = service.Read(pa, CatalogFamily.FeeType, privateFee.EntryId!.Value);
Check("owner can read the private fee", ownerRead.Status == ProposedStatus.Ok && ownerRead.Entry!.Code == "LATE");
Check("read projection has no actor, classification, or schema version",
    typeof(EntryProjection).GetProperties().All(property => property.Name is not "ActorUserId" and not "EconomicRole" and not "SchemaVersion")
    && !JsonSerializer.Serialize(ownerRead.Entry).Contains("economic_role", StringComparison.Ordinal)
    && !JsonSerializer.Serialize(ownerRead.Entry).Contains("\"Number\":0", StringComparison.Ordinal));
var saHistory = service.History(sa, CatalogFamily.FeeType, privateFee.EntryId!.Value);
Check("SA private fee history matches missing", OutcomeCompare.SameRefusal(saHistory, missingRead));
var ownerHistory = service.History(pa, CatalogFamily.FeeType, privateFee.EntryId!.Value);
Check("owner history omits the actor user id", ownerHistory.Status == ProposedStatus.Ok && ownerHistory.History!.Count == 1
    && ownerHistory.History[0].ChangeKind == "created" && typeof(HistoryProjection).GetProperties().All(property => property.Name != "ActorUserId"));

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
Check("fixtures are not audit events", repo.IntentCount == 3 && repo.HistoryCount == 3);
var protectedEdit = CatalogCommandParser.ParseClaimEdit("model_hardware_claim", claimId, "{\"expected_version\":1,\"model_id\":\"" + G(70) + "\"}");
Check("protected claim field is unsettled", protectedEdit is CatalogCommand.Rejected rejected && rejected.Status == ProposedStatus.Unprocessable && rejected.Code == "catalog_column_matrix_unsettled");
var unknownEdit = service.Execute(sa, Call("bad-claim"), CatalogCommandParser.ParseClaimEdit("model_hardware_claim", claimId, "{\"expected_version\":1,\"note\":\"x\"}"));
Check("unknown claim field writes nothing", unknownEdit.Code == "unknown_field" && repo.EntryVersionOrZero(claimId) == 1);
var delegated = service.Execute(sa, Call("delegated"), CatalogCommandParser.ParseClaimEdit("model_hardware_claim", claimId, "{\"expected_version\":1,\"claim_value\":\"next\",\"on_behalf_of\":\"" + G(77) + "\"}"));
Check("claim edit rejects delegation", delegated.Code == "authority_field_rejected" && repo.EntryVersionOrZero(claimId) == 1);
var comparisonsBefore = repo.VersionComparisons;
var hiddenClaim = service.Execute(otherPa, Call("hidden-claim"), CatalogCommandParser.ParseClaimEdit("model_hardware_claim", claimId, "{\"expected_version\":9,\"claim_value\":\"guess\"}"));
var missingClaim = service.Execute(otherPa, Call("missing-claim"), CatalogCommandParser.ParseClaimEdit("model_hardware_claim", G(81), "{\"expected_version\":9,\"claim_value\":\"guess\"}"));
Check("hidden and missing claim edits match and do not compare versions", OutcomeCompare.SameRefusal(hiddenClaim, missingClaim) && repo.VersionComparisons == comparisonsBefore);
var feeAsClaim = service.Execute(pa, Call("fee-as-claim"), CatalogCommandParser.ParseClaimEdit("model_hardware_claim", privateFee.EntryId!.Value, "{\"expected_version\":1,\"claim_value\":\"nope\"}"));
Check("a fee id on the claim command matches missing", OutcomeCompare.SameRefusal(feeAsClaim, missingClaim) && repo.VersionComparisons == comparisonsBefore);
var forbiddenClaim = service.Execute(pa, Call("global-claim"), CatalogCommandParser.ParseClaimEdit("model_hardware_claim", otherClaimId, "{\"expected_version\":1,\"claim_value\":\"nope\"}"));
Check("Partner Admin cannot edit a global claim, before version comparison", forbiddenClaim.Status == ProposedStatus.Forbidden && forbiddenClaim.Code == "catalog_claim_edit_actor" && repo.VersionComparisons == comparisonsBefore && repo.EntryVersionOrZero(otherClaimId) == 4);
var missingVersion = service.Execute(sa, Call("no-ver"), CatalogCommandParser.ParseClaimEdit("model_hardware_claim", claimId, "{\"claim_value\":\"next\"}"));
Check("missing claim version is a precondition failure", missingVersion.Status == ProposedStatus.PreconditionRequired && missingVersion.Code == "catalog_precondition_required");
var absentVersion = service.Execute(sa, Call("absent-ver"), CatalogCommandParser.ParseClaimEdit("model_hardware_claim", claimId, "{\"expected_version\":\"ABSENT\",\"claim_value\":\"next\"}"));
Check("ABSENT is not an entry precondition", absentVersion.Code == "version_domain" && repo.EntryVersionOrZero(claimId) == 1);
var stale = service.Execute(sa, Call("stale"), CatalogCommandParser.ParseClaimEdit("model_hardware_claim", claimId, "{\"expected_version\":2,\"claim_value\":\"next\"}"));
Check("stale claim version conflicts without a write", stale.Status == ProposedStatus.Conflict && stale.Code == "catalog_version_conflict" && !stale.Wrote && repo.EntryVersionOrZero(claimId) == 1 && repo.VersionComparisons == comparisonsBefore + 1);

var beforeEditIntents = repo.IntentCount;
var edited = service.Execute(sa, Call("edit-1", "corr-edit", "req-edit"), CatalogCommandParser.ParseClaimEdit("model_hardware_claim", claimId,
    "{\"expected_version\":1,\"claim_value\":\"next\",\"checked_at\":\"2026-11-08T12:00:00-07:00\"}"));
Check("claim edit commits the proposed allowlist and server actor", edited.Status == ProposedStatus.Ok && edited.EntryVersion == 2 && edited.Wrote
    && edited.Intent!.ProposedEventName == ProposedCatalogEvents.Updated
    && edited.Intent.Fields["actor_user_id"] == sa.UserId.ToString("D")
    && edited.Intent.ChangedFields().SequenceEqual(["checked_at", "claim_value"])
    && edited.Intent.Before!.Keys.OrderBy(key => key).SequenceEqual(edited.Intent.After.Keys.OrderBy(key => key))
    && !edited.Intent.After.ContainsKey("model_id")
    && edited.Intent.After["checked_at"]!.EndsWith('Z')
    && edited.Entry!.Fields["claim_value"] == "next"
    && repo.EntryVersionOrZero(claimId) == 2);
var uncheckedTime = CatalogCommandParser.ParseClaimEdit("model_hardware_claim", claimId, "{\"expected_version\":2,\"checked_at\":\"2026-11-08T12:00:00\"}");
Check("checked_at without an offset is rejected", uncheckedTime is CatalogCommand.Rejected);
var credentialUrl = CatalogCommandParser.ParseClaimEdit("model_hardware_claim", claimId, "{\"expected_version\":2,\"source_url\":\"https://user:secret@example.test/a\"}");
Check("source URL userinfo is rejected", credentialUrl is CatalogCommand.Rejected);
var replayEdit = service.Execute(sa, Call("edit-1", "other-corr", "other-req"), CatalogCommandParser.ParseClaimEdit("model_hardware_claim", claimId,
    "{\"expected_version\":1,\"claim_value\":\"next\",\"checked_at\":\"2026-11-08T12:00:00-07:00\"}"));
Check("claim replay returns the committed result for the same actor and does not append history", replayEdit.EntryVersion == 2 && replayEdit.Intent!.EventId == edited.Intent!.EventId && repo.IntentCount == beforeEditIntents + 1);
var noop = service.Execute(sa, Call("noop"), CatalogCommandParser.ParseClaimEdit("model_hardware_claim", claimId, "{\"expected_version\":2,\"claim_value\":\"next\"}"));
Check("same-state claim edit is a no-op, not a replayed success", noop.Code == "catalog_noop" && noop.Status == ProposedStatus.Conflict && !noop.Wrote && repo.EntryVersionOrZero(claimId) == 2 && repo.IntentCount == beforeEditIntents + 1);

var offReason = new string('r', 501);
var off = service.Execute(sa, Call("off"), CatalogCommandParser.ParseEntryActivation("fee_type", globalId, "{\"desired_active\":false,\"reason\":\"" + offReason + "\",\"expected_version\":9}"));
Check("a long Off reason is not held to the login bounds, and the Off writes nothing", off.Status == ProposedStatus.Unavailable && off.Gate == CatalogGates.GraphUnconfiguredAllOff && !off.Wrote
    && !JsonSerializer.Serialize(off).Contains(offReason, StringComparison.Ordinal) && repo.EntryVersionOrZero(globalId) == 1 && repo.Scopes.MutationCount == 0);
var shortOffHistory = repo.HistoryCount;
var shortOffIntents = repo.IntentCount;
var shortOffEntries = repo.EntryCount;
var shortOffKeys = repo.IdempotencyCount;
var shortOff = service.Execute(sa, Call("off-short"), CatalogCommandParser.ParseEntryActivation("fee_type", globalId, "{\"desired_active\":false,\"reason\":\"short\",\"expected_version\":\"ABSENT\"}"));
Check("a short Off reason is present enough and still writes nothing", shortOff.Status == ProposedStatus.Unavailable && shortOff.Gate == CatalogGates.GraphUnconfiguredAllOff && !shortOff.Wrote
    && repo.EntryVersionOrZero(globalId) == 1
    && repo.HistoryCount == shortOffHistory && repo.IntentCount == shortOffIntents
    && repo.EntryCount == shortOffEntries && repo.IdempotencyCount == shortOffKeys
    && repo.Scopes.Count == 0 && repo.Scopes.MutationCount == 0);
var blankReason = CatalogCommandParser.ParseEntryActivation("fee_type", globalId, "{\"desired_active\":false,\"reason\":\"   \",\"expected_version\":1}");
Check("blank reason is refused without importing a minimum length", blankReason is CatalogCommand.Rejected blank && blank.Code == "catalog_reason_required" && blank.Status == ProposedStatus.Unprocessable);
var capped = CatalogCommandParser.ParseEntryActivation("fee_type", globalId, "{\"desired_active\":false,\"reason\":\"" + new string('r', 4001) + "\",\"expected_version\":1}");
Check("the double input cap is not the owner reason policy", capped is CatalogCommand.Rejected cap && cap.Code == "double_input_cap" && cap.Detail == "double_input_cap_not_owner_reason_policy");
var restore = service.Execute(pa, Call("on"), CatalogCommandParser.ParseEntryActivation("fee_type", globalId, "{\"desired_active\":true,\"reason\":\"turn it back\",\"expected_version\":1}"));
Check("graph-dependent restore writes nothing", restore.Gate == CatalogGates.GraphDependentRestoreUnavailable && !restore.Wrote && repo.EntryVersionOrZero(globalId) == 1);

var saClaimOn = service.Execute(sa, Call("claim-on-sa"), CatalogCommandParser.ParseEntryActivation("model_hardware_claim", claimId, "{\"desired_active\":true,\"reason\":\"reactivate\",\"expected_version\":2}"));
var paClaimOn = service.Execute(pa, Call("claim-on-pa"), CatalogCommandParser.ParseEntryActivation("model_hardware_claim", claimId, "{\"desired_active\":true,\"reason\":\"reactivate\",\"expected_version\":2}"));
Check("claim reactivation is the same unavailable result for SA and the owning PA", OutcomeCompare.SameRefusal(saClaimOn, paClaimOn)
    && saClaimOn.Gate == CatalogGates.ClaimReactivationPendingPq272 && saClaimOn.Detail == "pending_pq272" && !saClaimOn.Wrote
    && !JsonSerializer.Serialize(saClaimOn).Contains("sa_only", StringComparison.OrdinalIgnoreCase)
    && !JsonSerializer.Serialize(saClaimOn).Contains("SA-only", StringComparison.Ordinal)
    && repo.EntryVersionOrZero(claimId) == 2);
var otherClaimOn = service.Execute(otherPa, Call("claim-on-other"), CatalogCommandParser.ParseEntryActivation("model_hardware_claim", claimId, "{\"desired_active\":true,\"reason\":\"reactivate\",\"expected_version\":2}"));
Check("a non-owner claim On matches missing", OutcomeCompare.SameRefusal(otherClaimOn, missingRead));
var claimOff = service.Execute(sa, Call("claim-off"), CatalogCommandParser.ParseEntryActivation("model_hardware_claim", claimId, "{\"desired_active\":false,\"reason\":\"deactivate\",\"expected_version\":2}"));
Check("claim Off is blocked because the graph is unknown", claimOff.Gate == CatalogGates.GraphUnconfiguredAllOff && !claimOff.Wrote);

var badPartner = service.Execute(sa, Call("bad-partner"), CatalogCommandParser.ParsePartnerActivation("fee_type", globalId, "not-a-uuid", "{\"desired_active\":false,\"reason\":\"stop\",\"expected_version\":\"ABSENT\"}"));
Check("a malformed partner id fails syntax and does not claim existence", badPartner.Code == "invalid_partner_uuid" && badPartner.PartnerTargetCheck is null
    && !JsonSerializer.Serialize(badPartner).Contains("exists", StringComparison.OrdinalIgnoreCase)
    && !JsonSerializer.Serialize(badPartner).Contains("verified", StringComparison.OrdinalIgnoreCase));
var syntaxPartner = service.Execute(sa, Call("syntax-partner"), CatalogCommandParser.ParsePartnerActivation("fee_type", globalId, G(30).ToString("D"), "{\"desired_active\":false,\"reason\":\"stop\",\"expected_version\":\"ABSENT\"}"));
Check("a well-formed SA partner id is syntax only", syntaxPartner.Gate == CatalogGates.GraphUnconfiguredAllOff
    && syntaxPartner.PartnerTargetCheck == CatalogCommandParser.PartnerTargetSyntaxOnly
    && !syntaxPartner.PartnerTargetCheck.Contains("verified", StringComparison.OrdinalIgnoreCase)
    && !syntaxPartner.Wrote && repo.Scopes.Count == 0);
var countBeforeRetarget = repo.EntryCount;
var retarget = service.Execute(pa, Call("retarget"), CatalogCommandParser.ParsePartnerActivation("fee_type", globalId, G(30).ToString("D"), "{\"desired_active\":false,\"reason\":\"stop\",\"expected_version\":\"ABSENT\"}"));
Check("Partner Admin cannot retarget the partner", retarget.Code == "contradictory_partner" && !retarget.Wrote && repo.EntryCount == countBeforeRetarget);
var privateSwitch = service.Execute(pa, Call("private-switch"), CatalogCommandParser.ParsePartnerActivation("fee_type", privateFee.EntryId!.Value, pa.PartnerId!.Value.ToString("D"), "{\"desired_active\":false,\"reason\":\"stop\",\"expected_version\":\"ABSENT\"}"));
Check("private partner-switch representation stays unresolved", privateSwitch.Gate == CatalogGates.PrivatePartnerSwitchUnresolved && privateSwitch.Detail == OpenPolicies.PrivatePartnerSwitch && !privateSwitch.Wrote);
var saLocation = service.Execute(sa, Call("sa-loc"), CatalogCommandParser.ParseLocationActivation("fee_type", globalId, G(40).ToString("D"), "{\"desired_active\":false,\"reason\":\"stop\",\"expected_version\":\"ABSENT\"}"));
Check("SA has no location switch", saLocation.Status == ProposedStatus.Forbidden && saLocation.Code == "sa_has_no_location_switch" && !saLocation.Wrote);
var paLocation = service.Execute(pa, Call("pa-loc"), CatalogCommandParser.ParseLocationActivation("fee_type", globalId, G(40).ToString("D"), "{\"desired_active\":true,\"reason\":\"resume\",\"expected_version\":\"ABSENT\"}"));
Check("location membership is not verified and restore stays unavailable", paLocation.Gate == CatalogGates.GraphDependentRestoreUnavailable && paLocation.LocationMembership == "not_verified" && !paLocation.Wrote);
var badLocation = CatalogCommandParser.ParseLocationActivation("fee_type", globalId, "loc", "{\"desired_active\":false,\"reason\":\"stop\",\"expected_version\":\"ABSENT\"}");
Check("malformed location id is rejected", badLocation is CatalogCommand.Rejected);
var hiddenOff = service.Execute(otherPa, Call("hidden-off"), CatalogCommandParser.ParseEntryActivation("fee_type", privateFee.EntryId!.Value, "{\"desired_active\":false,\"reason\":\"stop\",\"expected_version\":3}"));
Check("hidden Off matches missing", OutcomeCompare.SameRefusal(hiddenOff, missingRead));

var previewBefore = repo.HistoryCount;
var missingPreview = service.Preview(sa, CatalogFamily.FeeType, G(82), GraphView.Default);
var hiddenPreview = service.Preview(sa, CatalogFamily.FeeType, privateFee.EntryId!.Value, new GraphView.Fixture("rev-empty", []));
Check("hidden and missing previews match", OutcomeCompare.SameRefusal(missingPreview, hiddenPreview) && missingPreview.Preview is null);
var unknownPreview = service.Preview(sa, CatalogFamily.FeeType, globalId, null);
Check("UNCONFIGURED preview impact is unknown and not zero", unknownPreview.Preview!.Impact == "unknown" && unknownPreview.Preview.AffectedCount is null
    && unknownPreview.Preview.FixtureDeclaredEdgeCount is null && unknownPreview.Preview.ApplyAllowed == false && repo.HistoryCount == previewBefore);
var emptyFixture = new GraphView.Fixture("rev-empty", []);
var fixturePreview = service.Preview(sa, CatalogFamily.FeeType, globalId, emptyFixture);
Check("an empty fixture is not a zero-impact leaf and does not authorize", fixturePreview.Preview!.Impact == "fixture_not_authorization"
    && fixturePreview.Preview.AffectedCount is null && fixturePreview.Preview.FixtureDeclaredEdgeCount == 0
    && fixturePreview.Preview.AuthorizesWrites == false && fixturePreview.Preview.RuntimeConfiguration == false && !fixturePreview.Wrote);
var edged = service.DescribeGraph(new GraphView.Fixture("rev-edge", [new FixtureEdge(globalId, claimId)]));
Check("a fixture edge is not applied", edged.Graph!.FixtureDeclaredEdgeCount == 1 && edged.Graph.AffectedCount is null && edged.Graph.AuthorizesWrites == false
    && repo.EntryVersionOrZero(claimId) == 2 && repo.EntryVersionOrZero(globalId) == 1);
var unconfigured = service.DescribeGraph(null);
Check("the default graph is UNCONFIGURED", unconfigured.Graph!.State == "unconfigured" && unconfigured.Graph.Impact == "unknown" && unconfigured.Graph.AffectedCount is null);
var methodNames = typeof(CatalogDomainService).GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly).Select(method => method.Name).ToHashSet(StringComparer.Ordinal);
Check("the service has no runtime graph or cascade writer", !methodNames.Overlaps(["Configure", "SetGraph", "ApplyCascade", "Restore", "Save", "Migrate"]));

foreach (var family in CatalogFamilies.All)
{
    var fixtureId = Guid.NewGuid();
    if (family is CatalogFamily.FeeType or CatalogFamily.ModelHardwareClaim)
        continue;
    repo.InstallFixture(fixtureId, family, EntryClass.Global, null, "code-" + CatalogFamilies.Wire(family), "Name", null, 1, null);
    var familyOff = service.Execute(sa, Call("off-" + CatalogFamilies.Wire(family)), CatalogCommandParser.ParseEntryActivation(CatalogFamilies.Wire(family), fixtureId,
        "{\"desired_active\":false,\"reason\":\"stop this family\",\"expected_version\":1}"));
    Check($"{CatalogFamilies.Wire(family)} Off writes nothing while the graph is unknown", familyOff.Gate == CatalogGates.GraphUnconfiguredAllOff && !familyOff.Wrote && repo.EntryVersionOrZero(fixtureId) == 1);
}

var pan = "4111111111111111";
var sensitive = service.Execute(sa, Call("pan"), CatalogCommandParser.ParseCreate("fee_type", "{\"code\":\"panfee\",\"display_name\":\"" + pan + "\"}"));
Check("a card-shaped value is rejected without being echoed", sensitive.Code == "sensitive_value_rejected" && !JsonSerializer.Serialize(sensitive).Contains(pan, StringComparison.Ordinal));

var faultRepo = new InMemoryCatalogRepository { CommitFault = new InvalidOperationException("injected") };
var faultIds = new CountingIds();
var faultService = new CatalogDomainService(faultRepo, clock, faultIds);
var rolled = faultService.Execute(sa, Call("rollback"), CatalogCommandParser.ParseCreate("fee_type", "{\"code\":\"rollback\",\"display_name\":\"Rollback\"}"));
Check("a failed commit leaves no residue and is not a success replay", rolled.Code == "commit_rolled_back" && !rolled.Wrote && faultRepo.EntryCount == 0 && faultRepo.HistoryCount == 0 && faultRepo.IntentCount == 0 && faultRepo.IdempotencyCount == 0);
faultRepo.CommitFault = null;
var retried = faultService.Execute(sa, Call("rollback"), CatalogCommandParser.ParseCreate("fee_type", "{\"code\":\"rollback\",\"display_name\":\"Rollback\"}"));
Check("the same key can commit after rollback", retried.Status == ProposedStatus.Created && faultRepo.EntryCount == 1 && faultRepo.HistoryCount == 1 && faultRepo.IntentCount == 1);

var parallelRepo = new InMemoryCatalogRepository();
var parallelService = new CatalogDomainService(parallelRepo, clock, new CountingIds());
var entered = new ManualResetEventSlim(false);
var release = new ManualResetEventSlim(false);
var commitEntries = 0;
parallelRepo.BeforeCommit = () =>
{
    Interlocked.Increment(ref commitEntries);
    entered.Set();
    release.Wait();
};
var parallelCall = Call("parallel");
var parallelCommand = CatalogCommandParser.ParseCreate("fee_type", "{\"code\":\"parallel\",\"display_name\":\"Parallel\"}");
var first = Task.Run(() => parallelService.Execute(sa, parallelCall, parallelCommand));
var second = Task.Run(() => parallelService.Execute(sa, parallelCall, parallelCommand));
Check("parallel same key reaches the commit once", entered.Wait(TimeSpan.FromSeconds(5)) && parallelRepo.HistoryCount == 0);
release.Set();
var finished = Task.WaitAll([first, second], TimeSpan.FromSeconds(5));
Check("parallel same key commits one result", finished && commitEntries == 1 && first.Result.EntryId == second.Result.EntryId && first.Result.Wrote && second.Result.Wrote
    && parallelRepo.HistoryCount == 1 && parallelRepo.IntentCount == 1 && parallelRepo.CommitAttempts == 1);

try
{
    AuditIntent.Create(G(90), G(91), G(92), ProposedCatalogEvents.Created, sa, "fee_type", "entry", 1, ["code"], null,
        new Dictionary<string, string?> { ["code"] = "x" }, null, "c", "r", clock.UtcNow, AuditAllowlist.FeeValueKeys,
        schemaVersion: FrozenAuditSchemaBoundary.RejectedSentinel);
    Check("emitting the unapproved schema version throws", false);
}
catch (CatalogSchemaEmissionRejectedException)
{
    Check("emitting the unapproved schema version throws", true);
}
Check("a numeric schema version is also not emitted", Throws<CatalogSchemaEmissionRejectedException>(() => AuditIntent.Create(
    G(90), G(91), G(92), ProposedCatalogEvents.Created, sa, "fee_type", "entry", 1, ["code"], null,
    new Dictionary<string, string?> { ["code"] = "x" }, null, "c", "r", clock.UtcNow, AuditAllowlist.FeeValueKeys, schemaVersion: "1.2.0")));
Check("DURABLY_ACCEPTED cannot be claimed", Throws<CatalogSchemaEmissionRejectedException>(() => AuditIntent.Create(
    G(90), G(91), G(92), ProposedCatalogEvents.Created, sa, "fee_type", "entry", 1, ["code"], null,
    new Dictionary<string, string?> { ["code"] = "x" }, null, "c", "r", clock.UtcNow, AuditAllowlist.FeeValueKeys,
    statusOverride: FrozenAuditSchemaBoundary.DurableAcceptedNotClaimed)));
Check("activation and denial names are not recorded", Throws<CatalogSchemaEmissionRejectedException>(() => AuditIntent.Create(
    G(90), G(91), G(92), ProposedCatalogEvents.EntryActivation, sa, "fee_type", "entry", 1, ["code"], null,
    new Dictionary<string, string?> { ["code"] = "x" }, null, "c", "r", clock.UtcNow, AuditAllowlist.FeeValueKeys))
    && Throws<CatalogSchemaEmissionRejectedException>(() => AuditIntent.Create(
        G(90), G(91), G(92), ProposedCatalogEvents.Denied, sa, "fee_type", "entry", 1, ["code"], null,
        new Dictionary<string, string?> { ["code"] = "x" }, null, "c", "r", clock.UtcNow, AuditAllowlist.FeeValueKeys)));
Check("an outside before/after key is rejected", Throws<CatalogSchemaEmissionRejectedException>(() => AuditIntent.Create(
    G(90), G(91), G(92), ProposedCatalogEvents.Updated, sa, "model_hardware_claim", "entry", 2, ["model_id"], null,
    new Dictionary<string, string?> { ["model_id"] = G(93).ToString("D") }, null, "c", "r", clock.UtcNow, AuditAllowlist.ClaimEditValueKeys)));
var intentJson = JsonSerializer.Serialize(repo.Intents.Concat(faultRepo.Intents).Concat(parallelRepo.Intents));
Check("stored intents never contain the sentinel, a schema version, or durable acceptance",
    !intentJson.Contains(FrozenAuditSchemaBoundary.RejectedSentinel, StringComparison.Ordinal)
    && !intentJson.Contains("schema_version", StringComparison.Ordinal)
    && !intentJson.Contains(FrozenAuditSchemaBoundary.DurableAcceptedNotClaimed, StringComparison.Ordinal)
    && repo.Intents.All(intent => intent.Fields.Keys.OrderBy(key => key, StringComparer.Ordinal).SequenceEqual(AuditAllowlist.IntentKeys.OrderBy(key => key, StringComparer.Ordinal))));
Check("open policy ids stay recorded and unresolved", OpenPolicies.ClaimReactivation == "PQ-272" && OpenPolicies.GraphEdges == "PQ-263.1"
    && OpenPolicies.PrivateCode == "PQ-252.SA6" && OpenPolicies.FieldApproval == "PQ-252.SA1");
Check("duplicate JSON keys are rejected", CatalogCommandParser.ParseCreate("fee_type", "{\"code\":\"a\",\"code\":\"b\",\"display_name\":\"A\"}") is CatalogCommand.Rejected duplicate && duplicate.Code == "invalid_dto" && duplicate.Detail == "duplicate_field");
Check("repository scope table was not used by a command", repo.Scopes.MutationCount == 0 && repo.Scopes.Count == 0 && !repo.Scopes.ContainsZero);

Console.WriteLine($"Catalog.Contracts checks: {pass} passed, {fail.Count} failed " + (fail.Count == 0 ? "ALL PASS" : "FAILED: " + string.Join("; ", fail)));
return fail.Count == 0 ? 0 : 1;

static JsonElement Json(string text) => JsonDocument.Parse(text).RootElement.Clone();

static string Sha256(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

static int Count(string text, string value)
{
    var count = 0;
    var index = 0;
    while ((index = text.IndexOf(value, index, StringComparison.Ordinal)) >= 0)
    {
        count++;
        index += value.Length;
    }
    return count;
}

static bool Throws<T>(Action action) where T : Exception
{
    try
    {
        action();
        return false;
    }
    catch (T)
    {
        return true;
    }
}

static CatalogCall Call(string key, string correlation = "corr", string request = "req") => new(key, correlation, request);

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
            && File.Exists(Path.Combine(dir.FullName, "src", "Catalog.Contracts", "Catalog.Contracts.csproj")))
            return dir.FullName;
        dir = dir.Parent;
    }
    throw new InvalidOperationException("Repository root was not found from the check output directory.");
}

sealed class ManualClock(DateTimeOffset start) : ICatalogClock
{
    public DateTimeOffset UtcNow { get; set; } = start;
}

sealed class CountingIds : ICatalogIds
{
    private int _n;
    public int Taken { get; private set; }

    public Guid NewId()
    {
        lock (this)
        {
            Taken++;
            _n++;
            var bytes = new byte[16];
            BitConverter.TryWriteBytes(bytes, _n);
            bytes[15] = 0x5c;
            return new Guid(bytes);
        }
    }
}

internal static class CheckExtensions
{
    public static string[] ChangedFields(this AuditIntent intent) =>
        intent.Fields["changed_fields"]!.Split(',', StringSplitOptions.RemoveEmptyEntries);
}
