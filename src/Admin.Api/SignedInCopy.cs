namespace Admin.Api;

/// <summary>
/// Header copy from the resolved grant. There is no display-name column.
/// super_admin, partner_admin, and client_admin are the role_wire values resolve_grant returns.
/// </summary>
public static class SignedInCopy
{
    public static string Role(BuiltInRole role) => role switch
    {
        BuiltInRole.SuperAdmin => "Super Admin",
        BuiltInRole.PartnerAdmin => "Partner Admin",
        BuiltInRole.ClientAdmin => "Client Admin",
        _ => "",
    };
}
