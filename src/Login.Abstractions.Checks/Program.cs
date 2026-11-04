using System.Text.Json;
using Login.Abstractions;

var pass = 0; var fail = new List<string>();
void Check(string name, bool ok) { Console.WriteLine((ok ? "PASS " : "FAIL ") + name); if (ok) pass++; else fail.Add(name); }
bool Throws<T>(Action a) where T : Exception { try { a(); return false; } catch (T) { return true; } }

// Status mapping (TLD 7:53): exactly active|disabled; anything else throws, never Active.
Check("status 'active' -> Active", UserStatusText.Parse("active") == UserStatus.Active);
Check("status 'disabled' -> Disabled", UserStatusText.Parse("disabled") == UserStatus.Disabled);
foreach (var bad in new string?[] { null, "", "Active", "ACTIVE", " active", "active ", "locked", "pending", "enabled" })
    Check($"status {(bad is null ? "NULL" : "'" + bad + "'")} throws", Throws<InvalidOperationException>(() => UserStatusText.Parse(bad)));

// Fail-closed user rule (TLD 7:45 F1).
var now = DateTimeOffset.Parse("2026-11-04T16:00:00Z");
var u = Guid.Parse("5f0c3e1a-0003-4000-8000-000000000c01"); var p = Guid.Parse("5f0c3e1a-0001-4000-8000-000000000001");
Check("active, no lock may sign in", new ExternalLoginMatch(u, p, UserStatus.Active, null).MaySignIn(now));
Check("active, lock expired may sign in", new ExternalLoginMatch(u, p, UserStatus.Active, now.AddSeconds(-1)).MaySignIn(now));
Check("active, locked_until == now may sign in (> now refuses)", new ExternalLoginMatch(u, p, UserStatus.Active, now).MaySignIn(now));
Check("active, locked_until > now refused", !new ExternalLoginMatch(u, p, UserStatus.Active, now.AddMinutes(5)).MaySignIn(now));
Check("disabled refused", !new ExternalLoginMatch(u, p, UserStatus.Disabled, null).MaySignIn(now));
Check("disabled + expired lock refused", !new ExternalLoginMatch(u, p, UserStatus.Disabled, now.AddDays(-1)).MaySignIn(now));

// provider_kind mapping (F15).
Check("provider 'entra' -> Entra", ProviderKindText.Parse("entra") == ProviderKind.Entra);
Check("provider 'database' -> Database", ProviderKindText.Parse("database") == ProviderKind.Database);
Check("provider 'Entra' throws", Throws<InvalidOperationException>(() => ProviderKindText.Parse("Entra")));
Check("provider ToDb lower case", ProviderKindText.ToDb(ProviderKind.Entra) == "entra" && ProviderKindText.ToDb(ProviderKind.Database) == "database");

// Slug normalisation (spec §9).
Check("slug trims and lower-cases", LocationSlug.TryNormalize("  XK4M-9q2c ") == "xk4m-9q2c");
Check("slug 63 ok", LocationSlug.TryNormalize(new string('a', 63)) == new string('a', 63));
foreach (var bad in new string?[] { null, "", "   ", new string('a', 64), "-abc", "a_b", "a b", "ünï", "a.b", "a/b", "\u212Aelvin" })
    Check($"slug {(bad is null ? "NULL" : "'" + bad + "'")} -> null", LocationSlug.TryNormalize(bad) is null);

// SecretRef never printed or serialised (F13).
var cfg = new LocationLoginConfig(Guid.NewGuid(), p, "pci-main", ProviderKind.Entra, "PCI", new[] { Guid.NewGuid() },
    "https://login.microsoftonline.com", "5f4e3d2c-1b0a-4f9e-8d7c-6b5a4f3e2d1c",
    "arn:aws:secretsmanager:us-west-2:000000000000:secret:FAKE-test-only/x-AbCdEf", false, true, 3);
Check("ToString hides SecretRef", !cfg.ToString().Contains("arn:", StringComparison.Ordinal) && cfg.ToString().Contains("[redacted]", StringComparison.Ordinal));
var json = JsonSerializer.Serialize(cfg);
Check("System.Text.Json omits SecretRef", !json.Contains("SecretRef", StringComparison.Ordinal) && !json.Contains("arn:", StringComparison.Ordinal));

Console.WriteLine($"Login.Abstractions checks: {pass} passed, {fail.Count} failed " + (fail.Count == 0 ? "ALL PASS" : "FAILED: " + string.Join("; ", fail)));
return fail.Count == 0 ? 0 : 1;
