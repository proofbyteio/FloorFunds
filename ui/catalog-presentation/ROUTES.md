# Routes and interactions

Hash routes on `ui/catalog-presentation/index.html`. All data is synthetic and session-local.

| Route | Interaction |
|---|---|
| `#/overview` | Links to all 20 catalog lists and the shared checks. |
| `#/map` | Route index and the unimplemented list. |
| `#/family/{wire}/list` | Search, scope filter, empty state, TEST-ONLY rows, links to view, edit, Off, and On. |
| `#/family/{wire}/view/{id}` | Read-only identity. Activations are ABSENT. Availability is unknown. |
| `#/family/{wire}/add` | Names-only fee create for a management principal. Every other create stays unavailable and writes nothing. |
| `#/family/{wire}/edit/{id}` | Non-claim metadata save is refused. Claim edit shows the six fields disabled and does not persist. |
| `#/family/{wire}/off/{id}` | Reason check. UNCONFIGURED graph returns 503 and writes nothing. Apply stays disabled. Affected count is unavailable. |
| `#/family/{wire}/on/{id}` | Claim On is 503 for every actor who can address the claim. Other On requests are graph-dependent restore, also 503. |
| `#/family/{wire}/partner/{id}` | Partner UUID is syntax only. A private target returns the unresolved partner-switch gate. |
| `#/family/{wire}/location/{id}` | Super Admin receives no location switch. Partner Admin membership is not verified. |
| `#/family/{wire}/history/{id}` | History omits the actor user id. Fixture rows have no history. |
| `#/shared/same-code` | Manufacturer rows with `EXAMPLE-5-A` for the selected principal only. The owning Partner Admin sees both identities. Every other principal does not receive the private row. |
| `#/shared/empty` | Search that matches nothing. Clear filters restores the TEST-ONLY rows. |
| `#/shared/denied` | Cashier list is 403 `no_management_grant`. Add is omitted on that refused list. |
| `#/shared/graph` | UNCONFIGURED impact is unknown, not zero. A fixture description does not authorize a write. |

Changing the synthetic principal updates which rows are visible. It is not authentication.

Reference flows that stay unavailable rather than following the static mock into a successful apply: shared Off/cascade screens 27 and 35–40, per-value restore 37, and private Add screens 20–25 except the names-only fee form. The catalog-08 supersede-as-new control is not present.
