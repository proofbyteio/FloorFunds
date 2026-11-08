/** Catalog family metadata for the synthetic presentation. Not a policy approval. */

export const OWN_PARTNER = "20000000-0000-4000-8000-000000000001";
export const OTHER_PARTNER = "20000000-0000-4000-8000-000000000002";

export const ACTORS = {
  super_admin: { role: "super_admin", userId: "30000000-0000-4000-8000-000000000001", partnerId: null, label: "Super Admin" },
  partner_admin: { role: "partner_admin", userId: "30000000-0000-4000-8000-000000000002", partnerId: OWN_PARTNER, label: "Partner Admin (own test partner)" },
  partner_admin_other: { role: "partner_admin", userId: "30000000-0000-4000-8000-000000000003", partnerId: OTHER_PARTNER, label: "Partner Admin (other test partner)" },
  client_admin: { role: "client_admin", userId: "30000000-0000-4000-8000-000000000004", partnerId: null, label: "Client Admin" },
  supervisor: { role: "supervisor", userId: "30000000-0000-4000-8000-000000000005", partnerId: null, label: "Supervisor" },
  cashier: { role: "cashier", userId: "30000000-0000-4000-8000-000000000006", partnerId: null, label: "Cashier" },
  auditor: { role: "auditor", userId: "30000000-0000-4000-8000-000000000007", partnerId: null, label: "Auditor" },
  accounting: { role: "accounting", userId: "30000000-0000-4000-8000-000000000008", partnerId: null, label: "Accounting" },
  support: { role: "support", userId: "30000000-0000-4000-8000-000000000009", partnerId: null, label: "Support" },
  none: { role: "none", userId: "30000000-0000-4000-8000-00000000000a", partnerId: null, label: "No role" },
};

export const FAMILIES = [
  { n: 1, wire: "catalog_release", title: "Catalog release", group: "Master data", privateAdd: false, disposition: "required_fields_unresolved" },
  { n: 2, wire: "transaction_type", title: "Transaction type", group: "Transactions", privateAdd: false, disposition: "create_fields_not_closed" },
  { n: 3, wire: "channel", title: "Channel", group: "Transactions", privateAdd: false, disposition: "create_fields_not_closed" },
  { n: 4, wire: "device_category", title: "Device category", group: "Terminal dimensions", privateAdd: false, disposition: "create_fields_not_closed" },
  { n: 5, wire: "manufacturer", title: "Manufacturer", group: "Terminal dimensions", privateAdd: true, disposition: "create_fields_not_closed" },
  { n: 6, wire: "device_model", title: "Device model", group: "Terminal dimensions", privateAdd: true, disposition: "create_fields_not_closed" },
  { n: 7, wire: "hardware_capability_kind", title: "Hardware capability kind", group: "Terminal dimensions", privateAdd: false, disposition: "create_fields_not_closed" },
  { n: 8, wire: "model_hardware_claim", title: "Model hardware claim", group: "Terminal dimensions", privateAdd: false, disposition: "create_fields_not_closed", claim: true },
  { n: 9, wire: "card_network", title: "Card network", group: "Card dimensions", privateAdd: false, disposition: "create_fields_not_closed" },
  { n: 10, wire: "card_funding_class", title: "Card funding class", group: "Card dimensions", privateAdd: false, disposition: "create_fields_not_closed" },
  { n: 11, wire: "payment_rail", title: "Payment rail", group: "Card dimensions", privateAdd: false, disposition: "create_fields_not_closed" },
  { n: 12, wire: "check_instrument_kind", title: "Check instrument kind", group: "Check dimensions", privateAdd: false, disposition: "create_fields_not_closed" },
  { n: 13, wire: "check_drawer_class", title: "Check drawer class", group: "Check dimensions", privateAdd: false, disposition: "create_fields_not_closed" },
  { n: 14, wire: "check_purpose", title: "Check purpose", group: "Check dimensions", privateAdd: false, disposition: "create_fields_not_closed" },
  { n: 15, wire: "processor", title: "Processor", group: "Other directories", privateAdd: true, disposition: "create_fields_not_closed" },
  { n: 16, wire: "transaction_status", title: "Transaction status", group: "Transactions", privateAdd: false, disposition: "create_fields_not_closed" },
  { n: 17, wire: "reason_code", title: "Reason code", group: "Transactions", privateAdd: true, disposition: "create_fields_not_closed" },
  { n: 18, wire: "id_document_type", title: "ID document type", group: "Other directories", privateAdd: true, disposition: "required_fields_unresolved" },
  { n: 19, wire: "currency", title: "Currency", group: "Other directories", privateAdd: false, disposition: "required_fields_unresolved" },
  { n: 20, wire: "fee_type", title: "Fee type", group: "Other directories", privateAdd: true, disposition: "names_only" },
];

export const GROUPS = ["Master data", "Transactions", "Terminal dimensions", "Card dimensions", "Check dimensions", "Other directories"];

const WIRE = new Map(FAMILIES.map((family) => [family.wire, family]));

export function familyByWire(wire) {
  return WIRE.get(wire) ?? null;
}

export function padFamily(n) {
  return String(n).padStart(2, "0");
}

function idFor(n, kind, row) {
  return `11000000-0000-4000-8${String(n).padStart(3, "0")}-${kind}${String(row).padStart(11, "0")}`;
}

function globalRow(family, row, code, label) {
  return {
    entryId: idFor(family.n, "1", row),
    family: family.wire,
    entryClass: "global",
    ownerPartnerId: null,
    code,
    displayName: `TEST-ONLY ${label}`,
    description: "TEST-ONLY fixture row. Not a production seed.",
    entryVersion: 1,
    fields: {},
    origin: "fixture",
  };
}

function privateRow(family, row, code, label, partnerId) {
  return {
    entryId: idFor(family.n, "2", row),
    family: family.wire,
    entryClass: "private",
    ownerPartnerId: partnerId,
    code,
    displayName: `TEST-ONLY ${label}`,
    description: "TEST-ONLY private fixture. Not a production seed.",
    entryVersion: 1,
    fields: {},
    origin: "fixture",
  };
}

/** Explicit synthetic rows. Owner-selected names are not used as loaded records. */
export function seedEntries() {
  const rows = [];
  for (const family of FAMILIES) {
    if (family.claim) continue;
    rows.push(globalRow(family, 1, `EXAMPLE-${family.n}-A`, "example value A"));
    rows.push(globalRow(family, 2, `EXAMPLE-${family.n}-B`, "example value B"));
  }
  const manufacturer = familyByWire("manufacturer");
  rows.push(privateRow(manufacturer, 1, "EXAMPLE-5-A", "example private maker", OWN_PARTNER));
  for (const wire of ["device_model", "processor", "reason_code", "id_document_type"]) {
    const family = familyByWire(wire);
    rows.push(privateRow(family, 1, `EXAMPLE-${family.n}-P`, "example private value", OWN_PARTNER));
  }
  const fee = familyByWire("fee_type");
  rows.push(privateRow(fee, 1, "EXAMPLE-20-A", "example private fee", OWN_PARTNER));

  rows.push({
    entryId: idFor(8, "2", 1),
    family: "model_hardware_claim",
    entryClass: "private",
    ownerPartnerId: OWN_PARTNER,
    code: "EXAMPLE-CLAIM-P",
    displayName: "TEST-ONLY private claim",
    description: "TEST-ONLY claim fixture. Not a certification.",
    entryVersion: 1,
    origin: "fixture",
    fields: {
      model_id: "11111111-1111-4111-8111-111111111111",
      capability_entry_id: "22222222-2222-4222-8222-222222222222",
      option_key: "example-option",
      source_version: "example-v1",
      claim_value: "example-claim",
      availability: "example",
      source_url: "https://example.test/claim",
      checked_at: "2026-11-08T19:55:00.0000000Z",
    },
  });
  rows.push({
    entryId: idFor(8, "1", 1),
    family: "model_hardware_claim",
    entryClass: "global",
    ownerPartnerId: null,
    code: "EXAMPLE-CLAIM-G",
    displayName: "TEST-ONLY global claim",
    description: "TEST-ONLY global claim fixture.",
    entryVersion: 4,
    origin: "fixture",
    fields: {
      model_id: "33333333-3333-4333-8333-333333333333",
      capability_entry_id: "44444444-4444-4444-8444-444444444444",
      option_key: "example-global",
      source_version: "example-v4",
      claim_value: "example-global-claim",
      availability: "listed",
      source_url: "https://example.test/global-claim",
      checked_at: "2026-11-08T19:55:00.0000000Z",
    },
  });
  rows.push({
    entryId: idFor(8, "2", 2),
    family: "model_hardware_claim",
    entryClass: "private",
    ownerPartnerId: OTHER_PARTNER,
    code: "EXAMPLE-CLAIM-O",
    displayName: "TEST-ONLY other-partner claim",
    description: "TEST-ONLY claim owned by the other test partner.",
    entryVersion: 1,
    origin: "fixture",
    fields: {
      option_key: "example-other",
      source_version: "example-v1",
      claim_value: "example-other-claim",
      availability: "listed",
    },
  });
  return rows;
}

export const CLAIM_EDIT_KEYS = ["claim_value", "availability", "source_url", "checked_at", "display_name", "description"];
