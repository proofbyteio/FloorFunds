namespace Login.StandIn;

public static class OAuthSignOutProbe
{
    public static int Run()
    {
        var admin = OAuth.SignOutTarget(true, "/standin", "pci-main");
        var location = OAuth.SignOutTarget(false, "/standin", "pci-main");
        var ok = admin == "/signin" && !admin.Contains("/l/") && !admin.EndsWith("/admin")
            && location == "/standin/l/pci-main";
        Console.WriteLine((ok ? "PASS " : "FAIL ") + "admin-sign-out-goes-to-signin");
        return ok ? 0 : 1;
    }
}
