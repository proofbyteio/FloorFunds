namespace Login.StandIn;

/// <summary>
/// A location with no login_config cannot be signed into. Sign-in setup is a later action.
/// </summary>
public static class LocationSignIn
{
    public static bool Allowed(bool loginConfigPresent) => loginConfigPresent;
}
