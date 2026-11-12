// DEC-237. A missing location name is rejected by the existing create validator.
// This probe does not add a length, character, or uniqueness rule.
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
        return rejected && accepted ? 0 : 1;
    }

    static JsonElement Parse(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.Clone();
    }
}
