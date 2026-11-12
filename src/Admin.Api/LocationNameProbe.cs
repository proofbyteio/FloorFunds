// DEC-237. A missing location name is rejected by the existing create validator.
// The 200 Unicode-scalar upper bound is the existing validator limit, kept as a
// proposal pending PQ-313. This probe does not add a length, character, or
// uniqueness rule beyond that existing ValidateCreate check (F-24).
using System.Text;
using System.Text.Json;

namespace Admin.Api;

public static class LocationNameProbe
{
    public static int Run()
    {
        var bounds = new ReasonBounds(10, 500);
        var missing = Parse("""
            {"partner_id":"11111111-1111-4111-8111-111111111111","login_config":{"provider_kind":"database","display_name":"Staff","entra_allowed_tenant_ids":[],"allow_db_fallback":false,"mfa_required":true}}
            """);
        var errs = new List<FieldError>();
        var created = LoginConfigValidator.ValidateCreate(missing, bounds, errs, out _);
        var rejected = created is null && errs.Any(e => e.Field == "name" && e.Code == "required");
        Console.WriteLine((rejected ? "PASS " : "FAIL ") + "create-rejects-blank-location-name");
        errs.Clear();
        var named = Parse("""
            {"partner_id":"11111111-1111-4111-8111-111111111111","name":"North Desk","login_config":{"provider_kind":"database","display_name":"Staff","entra_allowed_tenant_ids":[],"allow_db_fallback":false,"mfa_required":true}}
            """);
        var ok = LoginConfigValidator.ValidateCreate(named, bounds, errs, out _);
        var accepted = ok is { Name: "North Desk" };
        Console.WriteLine((accepted ? "PASS " : "FAIL ") + "create-accepts-a-location-name");
        errs.Clear();
        var bare = Parse("""
            {"partner_id":"11111111-1111-4111-8111-111111111111","name":"North Desk"}
            """);
        var without = LoginConfigValidator.ValidateCreate(bare, bounds, errs, out _);
        var optional = without is { Name: "North Desk", Config: null } && errs.Count == 0;
        Console.WriteLine((optional ? "PASS " : "FAIL ") + "create-allows-a-location-without-login-config");

        // F-24. Astral (emoji) boundary: 200 Unicode scalars accepted, 201 rejected.
        // 😀 is one scalar and two UTF-16 code units, so .Length would be wrong.
        var emoji = "😀";
        var atLimit = Fill(emoji, 200);
        var over = Fill(emoji, 201);
        errs.Clear();
        var atOk = LoginConfigValidator.ValidateCreate(Named(atLimit), bounds, errs, out _);
        var atPass = atOk is not null && errs.Count == 0
            && atLimit.Length == 400 && atLimit.EnumerateRunes().Count() == 200;
        Console.WriteLine((atPass ? "PASS " : "FAIL ") + "server-accepts-200-astral-scalars");
        errs.Clear();
        var overOk = LoginConfigValidator.ValidateCreate(Named(over), bounds, errs, out _);
        var overPass = overOk is null && errs.Any(e => e.Field == "name" && e.Code == "name_invalid")
            && over.Length == 402 && over.EnumerateRunes().Count() == 201;
        Console.WriteLine((overPass ? "PASS " : "FAIL ") + "server-rejects-201-astral-scalars");

        return rejected && accepted && optional && atPass && overPass ? 0 : 1;
    }

    static string Fill(string unit, int runes)
    {
        var sb = new StringBuilder();
        for (var i = 0; i < runes; i++) sb.Append(unit);
        return sb.ToString();
    }

    static JsonElement Named(string name) => Parse(
        "{\"partner_id\":\"11111111-1111-4111-8111-111111111111\",\"name\":\"" + name
        + "\",\"address\":{\"address_line_1\":\"A\",\"city\":\"B\",\"country\":\"AC\",\"phone\":\"1\",\"time_zone\":\"UTC\"}}");

    static JsonElement Parse(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.Clone();
    }
}
