namespace Catalog.Contracts;

/// <summary>The twenty catalog families. Wire names are the closed path allowlist. No other name parses.</summary>
public enum CatalogFamily
{
    CatalogRelease = 1,
    TransactionType = 2,
    Channel = 3,
    DeviceCategory = 4,
    Manufacturer = 5,
    DeviceModel = 6,
    HardwareCapabilityKind = 7,
    ModelHardwareClaim = 8,
    CardNetwork = 9,
    CardFundingClass = 10,
    PaymentRail = 11,
    CheckInstrumentKind = 12,
    CheckDrawerClass = 13,
    CheckPurpose = 14,
    Processor = 15,
    TransactionStatus = 16,
    ReasonCode = 17,
    IdDocumentType = 18,
    Currency = 19,
    FeeType = 20,
}

public enum EntryClass
{
    Global = 1,
    Private = 2,
}

/// <summary>
/// Create-field disposition for this isolated slice. Release, ID document, and currency stay unavailable.
/// Fee is names only. Every other family's required create fields are not closed here, so those creates
/// stay unavailable rather than receiving a guessed DTO.
/// </summary>
public enum CreateFieldDisposition
{
    NamesOnlyFee = 1,
    UnresolvedRequiredFields = 2,
    FieldsNotClosed = 3,
}

public static class CatalogFamilies
{
    private static readonly CatalogFamily[] AllFamilies = Enum.GetValues<CatalogFamily>();

    private static readonly Dictionary<string, CatalogFamily> WireToFamily = new(StringComparer.Ordinal)
    {
        ["catalog_release"] = CatalogFamily.CatalogRelease,
        ["transaction_type"] = CatalogFamily.TransactionType,
        ["channel"] = CatalogFamily.Channel,
        ["device_category"] = CatalogFamily.DeviceCategory,
        ["manufacturer"] = CatalogFamily.Manufacturer,
        ["device_model"] = CatalogFamily.DeviceModel,
        ["hardware_capability_kind"] = CatalogFamily.HardwareCapabilityKind,
        ["model_hardware_claim"] = CatalogFamily.ModelHardwareClaim,
        ["card_network"] = CatalogFamily.CardNetwork,
        ["card_funding_class"] = CatalogFamily.CardFundingClass,
        ["payment_rail"] = CatalogFamily.PaymentRail,
        ["check_instrument_kind"] = CatalogFamily.CheckInstrumentKind,
        ["check_drawer_class"] = CatalogFamily.CheckDrawerClass,
        ["check_purpose"] = CatalogFamily.CheckPurpose,
        ["processor"] = CatalogFamily.Processor,
        ["transaction_status"] = CatalogFamily.TransactionStatus,
        ["reason_code"] = CatalogFamily.ReasonCode,
        ["id_document_type"] = CatalogFamily.IdDocumentType,
        ["currency"] = CatalogFamily.Currency,
        ["fee_type"] = CatalogFamily.FeeType,
    };

    private static readonly HashSet<CatalogFamily> PartnerAdminAdds =
    [
        CatalogFamily.Manufacturer,
        CatalogFamily.DeviceModel,
        CatalogFamily.Processor,
        CatalogFamily.ReasonCode,
        CatalogFamily.IdDocumentType,
        CatalogFamily.FeeType,
    ];

    public static IReadOnlyList<CatalogFamily> All => AllFamilies;

    public static bool TryParse(string? wire, out CatalogFamily family)
    {
        if (wire is null)
        {
            family = default;
            return false;
        }
        return WireToFamily.TryGetValue(wire, out family);
    }

    public static string Wire(CatalogFamily family) =>
        WireToFamily.First(pair => pair.Value == family).Key;

    public static bool IsPartnerAdminAddFamily(CatalogFamily family) => PartnerAdminAdds.Contains(family);

    public static CreateFieldDisposition Disposition(CatalogFamily family) => family switch
    {
        CatalogFamily.FeeType => CreateFieldDisposition.NamesOnlyFee,
        CatalogFamily.CatalogRelease or CatalogFamily.IdDocumentType or CatalogFamily.Currency
            => CreateFieldDisposition.UnresolvedRequiredFields,
        _ => CreateFieldDisposition.FieldsNotClosed,
    };
}
