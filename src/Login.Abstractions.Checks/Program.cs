using System.Text.Json;
using Login.Abstractions;

var pass = 0; var fail = new List<string>();
void Check(string name, bool ok) { Console.WriteLine((ok ? "PASS " : "FAIL ") + name); if (ok) pass++; else fail.Add(name); }
bool Throws<T>(Action a) where T : Exception { try { a(); return false; } catch (T) { return true; } }

// Status mapping (TLD 7:53): exactly active|disabled; anything else throws, never Active.
Check("status 'active' -> Active", UserStatusText.Parse("active") == UserStatus.Active);
Check("status 'disabled' -> Disabled", UserStatusText.Parse("disabled") == UserStatus.Disabled);
foreach (var bad in new string?[] { null, "", "unknown", "Unknown", "Active", "ACTIVE", " active", "active ", "locked", "pending", "enabled" })
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

// N1: default status is never Active.
Check("default(UserStatus) is Unknown", default(UserStatus) == UserStatus.Unknown);
Check("MaySignIn false for default(UserStatus)", !new ExternalLoginMatch(u, p, default, null).MaySignIn(now));
Check("MaySignIn false for a default ExternalLoginMatch", !new ExternalLoginMatch(default, default, default, default).MaySignIn(now));
Check("MaySignIn false for Unknown", !new ExternalLoginMatch(u, p, UserStatus.Unknown, null).MaySignIn(now));
Check("Parse never yields Unknown ('unknown' throws)", Throws<InvalidOperationException>(() => UserStatusText.Parse("unknown")));
Check("enum values explicit 0/1/2", (int)UserStatus.Unknown == 0 && (int)UserStatus.Active == 1 && (int)UserStatus.Disabled == 2);

// N2 / F13: SecretRef never printed, serialised, reflected or deconstructed.
const string arn = "arn:aws:secretsmanager:us-west-2:000000000000:secret:FAKE-test-only/x-AbCdEf";
var cfg = new LocationLoginConfig(Guid.NewGuid(), p, "pci-main", ProviderKind.Entra, "PCI", new[] { Guid.NewGuid() },
    "https://login.microsoftonline.com", "5f4e3d2c-1b0a-4f9e-8d7c-6b5a4f3e2d1c", false, true, 3) { SecretRef = new SecretReference(arn) };
Check("ToString hides SecretRef", !cfg.ToString().Contains("arn:", StringComparison.Ordinal) && cfg.ToString().Contains("[redacted]", StringComparison.Ordinal));
Check("SecretReference.ToString is [redacted]", cfg.SecretRef!.ToString() == "[redacted]");
Check("Reveal returns the value (resolver path)", cfg.SecretRef.Reveal() == arn);
var json = JsonSerializer.Serialize(cfg);
Check("System.Text.Json omits SecretRef", !json.Contains("SecretRef", StringComparison.Ordinal) && !json.Contains("arn:", StringComparison.Ordinal));
Check("System.Text.Json of SecretReference itself has no value", !JsonSerializer.Serialize(cfg.SecretRef).Contains("arn:", StringComparison.Ordinal));
// Reflection walk over public instance properties (what {@cfg}-style destructuring does), recursive.
static IEnumerable<string> Walk(object? o, int depth)
{
    if (o is null || depth > 4) yield break;
    var t = o.GetType();
    if (t.IsPrimitive || o is string || o is Guid || o is Enum || o is DateTimeOffset) { yield return o.ToString() ?? ""; yield break; }
    if (o is System.Collections.IEnumerable e) { foreach (var x in e) foreach (var s in Walk(x, depth + 1)) yield return s; yield break; }
    var props = t.GetProperties(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance).Where(pi => pi.GetIndexParameters().Length == 0).ToArray();
    if (props.Length == 0) { yield return o.ToString() ?? ""; yield break; }
    foreach (var pi in props) foreach (var s in Walk(pi.GetValue(o), depth + 1)) yield return pi.Name + "=" + s;
}
var walked = string.Join("|", Walk(cfg, 0));
Check("reflection property walk never yields the ARN", !walked.Contains("arn:", StringComparison.Ordinal) && walked.Contains("SecretRef=[redacted]", StringComparison.Ordinal));
Check("SecretReference has no public properties or fields", typeof(SecretReference).GetProperties().Length == 0 && typeof(SecretReference).GetFields().Length == 0);
var dec = typeof(LocationLoginConfig).GetMethods().Where(m => m.Name == "Deconstruct").ToArray();
Check("Deconstruct has no SecretRef slot", dec.Length == 1 && dec[0].GetParameters().All(pp => pp.Name != "SecretRef" && pp.ParameterType.GetElementType() != typeof(SecretReference)));
var (_, _, _, _, _, _, _, _, _, _, rv) = cfg;
Check("Deconstruct still works (11 positional values)", rv == 3);
Check("SecretReference rejects empty", Throws<ArgumentException>(() => new SecretReference(" ")));

Console.WriteLine($"Login.Abstractions checks: {pass} passed, {fail.Count} failed " + (fail.Count == 0 ? "ALL PASS" : "FAILED: " + string.Join("; ", fail)));
return fail.Count == 0 ? 0 : 1;
