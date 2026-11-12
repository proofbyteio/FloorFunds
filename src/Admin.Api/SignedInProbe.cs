namespace Admin.Api;

public static class SignedInProbe
{
    public static int Run()
    {
        var super = SignedInCopy.Role(BuiltInRole.SuperAdmin) == "Super Admin";
        var partner = SignedInCopy.Role(BuiltInRole.PartnerAdmin) == "Partner Admin";
        var none = SignedInCopy.Role(BuiltInRole.None) == "";
        var ok = super && partner && none;
        Console.WriteLine((ok ? "PASS " : "FAIL ") + "nav-role-is-the-resolved-display-name");
        return ok ? 0 : 1;
    }
}
