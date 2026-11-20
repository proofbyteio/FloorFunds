// REQ-068 read-only detail (DEC-289/294/296/297/303/309/315).

import {
  activateCopy,
  deactivateCopy,
  openOrgStatusDialog,
} from "./org-status-dialog.mjs";
import { fetchAllOrgRows } from "./org-list.mjs";

function h(tag, text) {
  const el = document.createElement(tag);
  if (text !== undefined) el.textContent = text;
  return el;
}

function statusBadge(status) {
  const active = status === "active";
  const span = h("span", active ? "Active" : "Inactive");
  span.className = "badge " + (active ? "badge--active" : "badge--inactive");
  return span;
}

function dd(label, value, span = "span-4", extra) {
  const cell = h("div");
  cell.className = span;
  cell.append(h("p", label));
  cell.firstChild.className = "dt";
  const val = h("p");
  val.className = "dd" + (value ? "" : " none");
  if (typeof value === "string" || typeof value === "number") {
    val.textContent = value ? String(value) : "None";
  } else if (value) {
    val.append(value);
  } else {
    val.textContent = "None";
  }
  if (extra) val.append(extra);
  cell.append(val);
  return cell;
}

function section(title, id, gridChildren) {
  const sec = h("section");
  sec.className = "section";
  sec.setAttribute("aria-labelledby", id);
  sec.append(h("h2", title));
  sec.firstChild.className = "section-title";
  sec.firstChild.id = id;
  const grid = h("div");
  grid.className = "grid";
  for (const c of gridChildren) grid.append(c);
  sec.append(grid);
  return sec;
}

async function resolveAddressLabels(api, address) {
  const a = address || {};
  let countryLabel = a.country || "";
  let regionLabel = a.region || "";
  let tzLabel = a.time_zone || "";
  if (a.country) {
    try {
      const [countries, regions, zones] = await Promise.all([
        api("/api/lookups/countries"),
        api("/api/lookups/regions?country=" + encodeURIComponent(a.country)),
        api("/api/lookups/time-zones?country=" + encodeURIComponent(a.country)),
      ]);
      const c = (countries.body?.countries ?? []).find((x) => x.code === a.country);
      if (c?.label) countryLabel = c.label;
      const r = (regions.body?.regions ?? []).find((x) => x.code === a.region);
      if (r?.label) regionLabel = r.label;
      const z = (zones.body?.time_zones ?? []).find((x) => x.value === a.time_zone);
      if (z?.label) tzLabel = z.label;
    } catch {
      /* keep codes */
    }
  }
  return {
    countryLabel,
    regionLabel,
    tzLabel,
    line1: a.address_line_1 || "",
    line2: a.address_line_2 || "",
    city: a.city || "",
    postal: a.postal_code || "",
    phone: a.phone || "",
    email: a.email || "",
  };
}

function rightsFor(kind, role) {
  if (kind === "partner") {
    return {
      canEdit: role === "Super Admin",
      canStatus: role === "Super Admin",
    };
  }
  if (kind === "client") {
    const ok = role === "Super Admin" || role === "Partner Admin";
    return { canEdit: ok, canStatus: ok };
  }
  // location: SA/PA only (DEC-286 / DEC-291)
  const ok = role === "Super Admin" || role === "Partner Admin";
  return { canEdit: ok, canStatus: ok };
}

/**
 * Nearest inactive ancestor for DEC-315 blocked Activate.
 * @returns {Promise<{ kind: "partner"|"client", name: string } | null>}
 */
async function nearestInactiveAncestor(api, kind, row) {
  if (kind === "partner") return null;
  if (kind === "client") {
    const got = await api("/api/orgs/partners/" + encodeURIComponent(row.partner_id));
    if (got.status === 200 && got.body?.status === "inactive") {
      return { kind: "partner", name: got.body.name || row.partner_name || "Partner" };
    }
    return null;
  }
  // location: nearest inactive — client first, else partner (NOTES PROPOSED default).
  if (row.client_id) {
    const cGot = await api("/api/orgs/clients/" + encodeURIComponent(row.client_id));
    if (cGot.status === 200 && cGot.body?.status === "inactive") {
      return { kind: "client", name: cGot.body.name || row.client_name || "Client" };
    }
    const partnerId = cGot.body?.partner_id || row.partner_id;
    if (partnerId) {
      const pGot = await api("/api/orgs/partners/" + encodeURIComponent(partnerId));
      if (pGot.status === 200 && pGot.body?.status === "inactive") {
        return { kind: "partner", name: pGot.body.name || "Partner" };
      }
    }
  } else if (row.partner_id) {
    const pGot = await api("/api/orgs/partners/" + encodeURIComponent(row.partner_id));
    if (pGot.status === 200 && pGot.body?.status === "inactive") {
      return { kind: "partner", name: pGot.body.name || "Partner" };
    }
  }
  return null;
}

function blockedNote(ancestor) {
  const note = h("p");
  note.className = "blocked-note";
  note.id = "activate-blocked";
  note.innerHTML =
    '<svg width="16" height="16" viewBox="0 0 16 16" fill="none" aria-hidden="true">' +
    '<circle cx="8" cy="8" r="7" stroke="#d1b26f" stroke-width="1.5"/>' +
    '<path d="M8 7v4.5" stroke="#d1b26f" stroke-width="1.5" stroke-linecap="round"/>' +
    '<circle cx="8" cy="4.75" r="0.9" fill="#d1b26f"/></svg><span></span>';
  const entity = ancestor.kind === "partner" ? "client" : "location";
  note.querySelector("span").textContent =
    `${ancestor.name} is Inactive. Activate the ${ancestor.kind} first, then you can activate this ${entity}.`;
  return note;
}

/**
 * @param {{
 *   main: HTMLElement,
 *   api: Function,
 *   role: string,
 *   kind: "partner"|"client"|"location",
 *   id: string,
 *   denied: () => void,
 *   flash?: {kind:string,title:string,text:string}|null,
 *   clearFlash?: () => void,
 *   setCrumbs?: (parts: {label:string, href?: string}[]) => void,
 *   navSelf?: boolean,
 * }} ctx
 */
export async function showOrgDetail(ctx) {
  const { main, api, role, kind, id, denied, setCrumbs, navSelf } = ctx;
  const path =
    kind === "partner"
      ? "/api/orgs/partners/" + encodeURIComponent(id)
      : kind === "client"
        ? "/api/orgs/clients/" + encodeURIComponent(id)
        : "/api/orgs/locations/" + encodeURIComponent(id);
  const got = await api(path);
  if (got.status === 401) return;
  if (got.status === 403) return denied();
  if (got.status !== 200) {
    main.replaceChildren(h("h1", "Not found"), h("p", "That record is not available."));
    return;
  }
  const row = got.body;
  const name = row.name || "";
  const status = row.status === "inactive" ? "inactive" : "active";
  const rights = rightsFor(kind, role);
  const labels = await resolveAddressLabels(api, row.address);
  let ancestor = null;
  if (status === "inactive" && rights.canStatus) {
    ancestor = await nearestInactiveAncestor(api, kind, row);
  }

  const listHref =
    kind === "partner" ? "#/partners" : kind === "client" ? "#/clients" : "#/locations";
  const listLabel =
    kind === "partner" ? "Partners" : kind === "client" ? "Clients" : "Locations";
  const selfLabel =
    kind === "partner" ? "My partner" : kind === "client" ? "My client" : listLabel;
  if (navSelf) setCrumbs?.([{ label: selfLabel }]);
  else setCrumbs?.([{ label: listLabel, href: listHref }, { label: name }]);

  main.replaceChildren();
  if (ctx.flash) {
    const b = h("div");
    b.className = "banner banner--" + ctx.flash.kind;
    b.setAttribute("role", "status");
    b.append(h("span", ctx.flash.title));
    if (ctx.flash.text) b.append(h("p", ctx.flash.text));
    main.append(b);
    ctx.clearFlash?.();
  }

  const pageHead = h("div");
  pageHead.className = "page-head with-action";
  pageHead.style.maxWidth = "var(--form-card-width)";
  const headTitle = h("div");
  headTitle.className = "head-title";
  headTitle.append(h("h1", name), statusBadge(status));
  pageHead.append(headTitle);

  const actions = h("div");
  actions.className = "head-actions";

  if (rights.canStatus) {
    if (status === "active") {
      const deact = h("button", "Deactivate");
      deact.type = "button";
      deact.className = "btn-danger";
      deact.addEventListener("click", () => void onDeactivate());
      actions.append(deact);
    } else if (ancestor) {
      const btn = h("button", "Activate");
      btn.type = "button";
      btn.className = "btn-disabled";
      btn.setAttribute("aria-disabled", "true");
      btn.setAttribute("aria-describedby", "activate-blocked");
      actions.append(btn);
    } else {
      const act = h("button", "Activate");
      act.type = "button";
      act.className = "button button--secondary btn-secondary";
      act.addEventListener("click", () => void onActivate());
      actions.append(act);
    }
  }

  // DEC-309: hide Edit when inactive.
  if (rights.canEdit && status === "active") {
    const edit = h("a", "Edit");
    edit.className = "button button--secondary btn-secondary";
    edit.href =
      kind === "partner"
        ? `#/partners/${id}/edit`
        : kind === "client"
          ? `#/clients/${id}/edit`
          : `#/locations/${id}/edit`;
    actions.append(edit);
  }
  if (actions.childNodes.length) pageHead.append(actions);
  main.append(pageHead);

  if (ancestor) main.append(blockedNote(ancestor));

  const card = h("div");
  card.className = "detail-card dl";

  if (kind === "partner") {
    card.append(
      section("Partner", "d-sec-partner", [dd("Partner name", name, "span-6")]),
    );
  } else if (kind === "client") {
    const partnerCell = dd("Partner", row.partner_name || "", "span-4");
    if (ancestor?.kind === "partner") {
      const badge = statusBadge("inactive");
      badge.style.marginLeft = "8px";
      partnerCell.querySelector(".dd")?.append(document.createTextNode(" "), badge);
    }
    card.append(
      section("Client", "d-sec-client", [
        partnerCell,
        dd("Client name", name, "span-4"),
      ]),
    );
  } else {
    const partnerCell = dd("Partner", row.partner_name || "", "span-4");
    const clientCell = dd("Client", row.client_name || "", "span-4");
    if (ancestor?.kind === "partner") {
      const badge = statusBadge("inactive");
      badge.style.marginLeft = "8px";
      partnerCell.querySelector(".dd")?.append(document.createTextNode(" "), badge);
    }
    if (ancestor?.kind === "client") {
      const badge = statusBadge("inactive");
      badge.style.marginLeft = "8px";
      clientCell.querySelector(".dd")?.append(document.createTextNode(" "), badge);
    }
    // Location detail may not include partner_name; leave blank if absent.
    if (!row.partner_name && row.partner_id) {
      const pGot = await api("/api/orgs/partners/" + encodeURIComponent(row.partner_id));
      if (pGot.status === 200) {
        const ddEl = partnerCell.querySelector(".dd");
        if (ddEl) ddEl.textContent = pGot.body?.name || "";
      }
    }
    card.append(
      section("Location", "d-sec-location", [
        partnerCell,
        clientCell,
        dd("Location name", name, "span-6"),
      ]),
    );
  }

  const addrSpacer = h("div");
  addrSpacer.className = "span-4";
  card.append(
    section("Address", "d-sec-address", [
      dd("Country", labels.countryLabel, "span-4"),
      addrSpacer,
      dd("Street address line 1", labels.line1, "span-5"),
      dd("Street address line 2", labels.line2 || null, "span-3"),
      dd("City", labels.city, "span-3"),
      dd("State / province", labels.regionLabel, "span-3"),
      dd("ZIP / postal code", labels.postal, "span-2"),
    ]),
  );

  card.append(
    section("Contact", "d-sec-contact", [
      dd("Primary contact phone", labels.phone, "span-4"),
      dd("Primary contact email", labels.email || null, "span-4"),
    ]),
  );
  card.append(
    section("Time zone", "d-sec-tz", [dd("Time zone", labels.tzLabel, "span-6")]),
  );

  main.append(card);

  async function onDeactivate() {
    const previewPath =
      kind === "partner"
        ? `/api/orgs/partners/${encodeURIComponent(id)}/deactivate-preview`
        : kind === "client"
          ? `/api/orgs/clients/${encodeURIComponent(id)}/deactivate-preview`
          : `/api/orgs/locations/${encodeURIComponent(id)}/deactivate-preview`;
    const preview = await api(previewPath);
    const copy = deactivateCopy(kind, name, preview.status === 200 ? preview.body : {});
    openOrgStatusDialog({
      ...copy,
      onConfirm: async () => {
        const post =
          kind === "partner"
            ? `/api/orgs/partners/${encodeURIComponent(id)}/deactivate`
            : kind === "client"
              ? `/api/orgs/clients/${encodeURIComponent(id)}/deactivate`
              : `/api/orgs/locations/${encodeURIComponent(id)}/deactivate`;
        const res = await api(post, { method: "POST" });
        if (res.status >= 400) throw new Error("failed");
        if (typeof ctx.redraw === "function") await ctx.redraw();
        else location.reload();
      },
    });
  }

  async function onActivate() {
    const copy = activateCopy(kind, name);
    openOrgStatusDialog({
      ...copy,
      onConfirm: async () => {
        const post =
          kind === "partner"
            ? `/api/orgs/partners/${encodeURIComponent(id)}/activate`
            : kind === "client"
              ? `/api/orgs/clients/${encodeURIComponent(id)}/activate`
              : `/api/orgs/locations/${encodeURIComponent(id)}/activate`;
        const res = await api(post, { method: "POST" });
        if (res.status >= 400) throw new Error("failed");
        if (typeof ctx.redraw === "function") await ctx.redraw();
        else location.reload();
      },
    });
  }
}

/** Resolve the caller's single partner/client for my_partner / my_client. */
export async function resolveMyOrgId(api, kind) {
  const listKind = kind === "partner" ? "partners" : "clients";
  for (const status of ["active", "inactive"]) {
    const rows = await fetchAllOrgRows(api, listKind, { status });
    if (rows[0]) {
      const idKey = kind === "partner" ? "partner_id" : "client_id";
      return rows[0][idKey] || null;
    }
  }
  return null;
}
