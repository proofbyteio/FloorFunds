using System.Text.Json.Nodes;

namespace Login.StandIn;

public static class LocationSignInProbe
{
    public static int Run()
    {
        var fx = JsonNode.Parse("""
            {"partners":[{"partner_id":"5f0c3e1a-0001-4000-8000-000000000001","preset":"pci","locations":[
              {"slug":"open","location_id":"5f0c3e1a-0001-4000-8000-0000000000a1"},
              {"slug":"closed","location_id":"5f0c3e1a-0001-4000-8000-0000000000a2","login_config":false}
            ]}]}
            """)!.AsObject();
        var open = SignIn.Opens(SignIn.Location(fx, "open"));
        var closed = SignIn.Opens(SignIn.Location(fx, "closed"));
        var refused = open && !closed;
        Console.WriteLine((refused ? "PASS " : "FAIL ") + "location-without-login-config-cannot-sign-in");
        return refused ? 0 : 1;
    }
}
