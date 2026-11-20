namespace Admin.Api;

/// <summary>
/// Header copy from the resolved grant. There is no display-name column.
/// super_admin, partner_admin, client_admin, and supervisor (Location Admin) are the role_wire values.
/// </summary>
public static class SignedInCopy
{
    public static string Role(BuiltInRole role) => role switch
    {
        BuiltInRole.SuperAdmin => "Super Admin",
        BuiltInRole.PartnerAdmin => "Partner Admin",
        BuiltInRole.ClientAdmin => "Client Admin",
        BuiltInRole.LocationAdmin => "Location Admin",
        _ => "",
    };
}
