# Catalog presentation

Isolated synthetic screens for the accepted catalog contract. This directory is not referenced by the admin host, the HTTP transport, or a database project.

The screen logic is a fixture projection in `js/projection.mjs`. It does not call `CatalogDomainService`. A names-only fee create can add a row in the page session. Refresh discards it. Nothing is written to `localStorage`, an API, or a database.

## Run

From the repository root:

```
node --test ui/catalog-presentation/tests/*.test.mjs
```

Open `ui/catalog-presentation/index.html` through a local static server. Hash routes are listed in `ROUTES.md`.

## Contract precedence

Accepted contract gates override the frozen static screens where they disagree. In particular, rows are not shown as stored On or Off, Off and graph-dependent restore stay unavailable while the graph is UNCONFIGURED, and claim edit is ordinary same-ID editing with persistence disabled. `variant_note` and `supersedes_claim_id` are not fields.

## Not built

PQ-269, PQ-270, PQ-271, and PQ-272 behavior beyond the unavailable claim-On gate. Private partner-switch, further private-code policy, and private location-Off preservation. Release, ID-document, currency, and other non-fee creates. Metadata saves. Cascade apply, impact counts, authentication, seeds, and durable audit acceptance.
