namespace Admin.Api;

public static class CatalogNavProbe
{
    public static int Run()
    {
        var user = Guid.Parse("11111111-1111-4111-8111-111111111111");
        var partner = Guid.Parse("22222222-2222-4222-8222-222222222222");
        var other = Guid.Parse("33333333-3333-4333-8333-333333333333");
        var super = new AdminGrant(BuiltInRole.SuperAdmin, true, true, new HashSet<Guid>(), true);
        var one = new AdminGrant(BuiltInRole.PartnerAdmin, true, true, new HashSet<Guid>(), true, new HashSet<Guid> { partner });
        var many = new AdminGrant(BuiltInRole.PartnerAdmin, true, true, new HashSet<Guid>(), true, new HashSet<Guid> { partner, other });
        var shown = CatalogNav.Includes(user, super) && CatalogNav.Includes(user, one);
        var hidden = !CatalogNav.Includes(user, many) && !CatalogNav.Includes(user, AdminGrant.Nothing);
        Console.WriteLine((shown ? "PASS " : "FAIL ") + "catalog-nav-with-permission");
        Console.WriteLine((hidden ? "PASS " : "FAIL ") + "catalog-nav-hidden-without-permission");
        return shown && hidden ? 0 : 1;
    }
}
