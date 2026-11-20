// REQ-068 list screens (DEC-285…318): search, status filter, sort, pager, session restore.

const PAGE_SIZE = 10;
const SEARCH_DEBOUNCE_MS = 150;
const STORAGE_PREFIX = "ff.orglist.";

function h(tag, text) {
  const el = document.createElement(tag);
  if (text !== undefined) el.textContent = text;
  return el;
}

function primaryLink(href, text) {
  const a = h("a");
  a.href = href;
  a.className = "btn-primary";
  const press = h("span", text);
  press.className = "btn-primary__press";
  a.append(press);
  return a;
}

const SORT_UP =
  '<svg class="sort-ind" viewBox="0 0 10 10" aria-hidden="true"><path d="M5 2l3.5 5h-7z" fill="#d1b26f"/></svg>';
const SORT_BOTH =
  '<svg class="sort-ind" viewBox="0 0 10 10" aria-hidden="true"><path d="M5 1l3 3.6H2z M5 9L2 5.4h6z" fill="#9ca3af"/></svg>';
const SORT_DOWN =
  '<svg class="sort-ind" viewBox="0 0 10 10" aria-hidden="true"><path d="M5 8L1.5 3h7z" fill="#d1b26f"/></svg>';
const CHEV =
  '<svg width="8" height="12" viewBox="0 0 8 12" fill="none" aria-hidden="true"><path d="M1.5 1.5L6 6l-4.5 4.5" stroke="#9ca3af" stroke-width="1.5" stroke-linecap="round" stroke-linejoin="round"/></svg>';

/** @typedef {"partners" | "clients" | "locations"} OrgListKind */

const KIND = {
  partners: {
    title: "Partners",
    api: "/api/orgs/partners",
    idKey: "partner_id",
    detailHash: (id) => `#/partners/${id}`,
    createHash: "#/partners/new",
    createLabel: "Create new partner",
    createNeed: "create_partner",
    parentCol: null,
    parentSort: null,
    noun: "partners",
  },
  clients: {
    title: "Clients",
    api: "/api/orgs/clients",
    idKey: "client_id",
    detailHash: (id) => `#/clients/${id}`,
    createHash: "#/clients/new",
    createLabel: "Create new client",
    createNeed: "create_client",
    parentCol: "Partner",
    parentSort: "partner_name",
    parentField: "partner_name",
    noun: "clients",
  },
  locations: {
    title: "Locations",
    api: "/api/orgs/locations",
    idKey: "location_id",
    detailHash: (id) => `#/locations/${id}`,
    createHash: "#/locations/new",
    createLabel: "Create new location",
    createNeed: "create_location",
    parentCol: "Client",
    parentSort: "client_name",
    parentField: "client_name",
    noun: "locations",
  },
};

export function loadListState(kind) {
  try {
    const store = globalThis.sessionStorage;
    if (!store || typeof store.getItem !== "function") return null;
    const raw = store.getItem(STORAGE_PREFIX + kind);
    if (!raw) return null;
    const o = JSON.parse(raw);
    return {
      q: typeof o.q === "string" ? o.q : "",
      status: o.status === "inactive" ? "inactive" : "active",
      sort: typeof o.sort === "string" ? o.sort : "name",
      dir: o.dir === "desc" ? "desc" : "asc",
      page: Number.isFinite(o.page) && o.page >= 1 ? Math.floor(o.page) : 1,
    };
  } catch {
    return null;
  }
}

export function saveListState(kind, state) {
  try {
    const store = globalThis.sessionStorage;
    if (!store || typeof store.setItem !== "function") return;
    store.setItem(STORAGE_PREFIX + kind, JSON.stringify(state));
  } catch {
    /* ignore quota / missing store */
  }
}

export function defaultListState() {
  return { q: "", status: "active", sort: "name", dir: "asc", page: 1 };
}

/** ASSUMPTION: list APIs return { items, total, page, page_size }; legacy keys still accepted. */
export function listItemsFromBody(body, kind) {
  if (!body || typeof body !== "object") return { items: [], total: 0, page: 1 };
  const items = Array.isArray(body.items)
    ? body.items
    : Array.isArray(body[kind])
      ? body[kind]
      : [];
  const total = Number.isFinite(body.total) ? Number(body.total) : items.length;
  const page = Number.isFinite(body.page) ? Number(body.page) : 1;
  return { items, total, page };
}

/** Fetch every page for create-form dropdowns (API page_size is 10). */
export async function fetchAllOrgRows(api, kind, { status = "active" } = {}) {
  const meta = KIND[kind];
  const all = [];
  let page = 1;
  let total = Infinity;
  while (all.length < total && page < 100) {
    const qs = new URLSearchParams({
      status,
      sort: "name",
      dir: "asc",
      page: String(page),
    });
    const res = await api(meta.api + "?" + qs.toString());
    if (res.status !== 200) break;
    const parsed = listItemsFromBody(res.body, kind);
    all.push(...parsed.items);
    total = parsed.total;
    if (parsed.items.length === 0) break;
    page += 1;
  }
  return all;
}

export function listLead(kind, role, scopeName) {
  if (kind === "partners") return "All partners.";
  if (kind === "clients") {
    if (role === "Partner Admin" && scopeName) return `Clients under ${scopeName}.`;
    return "All clients.";
  }
  if (role === "Location Admin") return "Your locations.";
  if (role === "Client Admin" && scopeName) return `Locations under ${scopeName}.`;
  if (role === "Partner Admin" && scopeName) return `Locations under ${scopeName}.`;
  return "All locations.";
}

function pageNumbers(page, pages) {
  const nums = [];
  for (let p = 1; p <= pages; p++) {
    if (pages <= 7 || p === 1 || p === pages || Math.abs(p - page) <= 1) nums.push(p);
  }
  return nums;
}

function statusBadge(status) {
  const active = status === "active";
  const span = h("span", active ? "Active" : "Inactive");
  span.className = "badge " + (active ? "badge--active" : "badge--inactive");
  return span;
}

function emptyCard(title) {
  const card = h("div");
  card.className = "empty-card";
  card.innerHTML =
    '<svg width="40" height="40" viewBox="0 0 40 40" fill="none" aria-hidden="true">' +
    '<rect x="6" y="9" width="28" height="22" rx="4" stroke="#9ca3af" stroke-width="1.5"/>' +
    '<path d="M6 16h28" stroke="#9ca3af" stroke-width="1.5"/>' +
    '<path d="M14 23h12" stroke="#d1b26f" stroke-width="1.5" stroke-linecap="round"/></svg>';
  card.append(h("h2", title));
  card.append(h("p", "When a location is assigned to you, it will appear here."));
  return card;
}

/**
 * @param {{
 *   main: HTMLElement,
 *   api: (path: string, opts?: any) => Promise<{status:number, body?: any}>,
 *   grant: string[],
 *   role: string,
 *   kind: OrgListKind,
 *   denied: () => void,
 *   setCrumbs?: (parts: string[]) => void,
 * }} ctx
 */
export async function showOrgList(ctx) {
  const { main, api, grant, role, kind, denied, setCrumbs } = ctx;
  const meta = KIND[kind];
  const canCreate = grant.includes(meta.createNeed);
  // Backward compat: old create_* keys alone still open the list for SA/PA.
  const canOpen =
    grant.includes("list_" + kind) ||
    grant.includes(meta.createNeed) ||
    (kind === "locations" && (grant.includes("list_locations") || grant.includes("create_location")));
  if (!canOpen && kind === "partners" && !grant.includes("list_partners") && !grant.includes("create_partner")) {
    return denied();
  }
  if (!canOpen && kind === "clients" && !grant.includes("list_clients") && !grant.includes("create_client")) {
    return denied();
  }
  if (!canOpen && kind === "locations") return denied();

  let state = loadListState(kind) || defaultListState();
  // Clamp sort to allowed columns for this kind.
  const allowedSort = new Set(["name", "status"]);
  if (meta.parentSort) allowedSort.add(meta.parentSort);
  if (!allowedSort.has(state.sort)) state.sort = "name";

  let scopeName = "";
  // DEC-331: CA cannot GET /api/orgs/partners — do not probe partners for CA.
  if (role === "Partner Admin") {
    if (kind === "clients" || kind === "locations") {
      const partners = await fetchAllOrgRows(api, "partners", { status: "active" });
      if (!partners.length) {
        const inactive = await fetchAllOrgRows(api, "partners", { status: "inactive" });
        scopeName = inactive[0]?.name || "";
      } else {
        scopeName = partners[0]?.name || "";
      }
    }
  } else if (role === "Client Admin" && kind === "locations") {
    const clients = await fetchAllOrgRows(api, "clients", { status: "active" });
    if (!clients.length) {
      const inactive = await fetchAllOrgRows(api, "clients", { status: "inactive" });
      scopeName = inactive[0]?.name || "";
    } else {
      scopeName = clients[0]?.name || "";
    }
  }

  const lead = listLead(kind, role, scopeName);
  setCrumbs?.([meta.title]);

  let debounceTimer = 0;
  let requestSeq = 0;

  async function loadAndRender() {
    const seq = ++requestSeq;
    saveListState(kind, state);

    const qs = new URLSearchParams({
      status: state.status,
      sort: state.sort,
      dir: state.dir,
      page: String(state.page),
    });
    if (state.q.trim()) qs.set("q", state.q.trim());

    const res = await api(meta.api + "?" + qs.toString());
    if (seq !== requestSeq) return;
    if (res.status === 401) return;
    if (res.status === 403) return denied();
    if (res.status !== 200) {
      main.replaceChildren(h("h1", meta.title), h("p", "The list could not be loaded."));
      return;
    }

    const { items, total } = listItemsFromBody(res.body, kind);
    const isDefaultFilters = !state.q.trim() && state.status === "active" && state.sort === "name" && state.dir === "asc";

    // PO F-10 / DEC-241 PM default (PQ-361 dismissed): empty only when scope has
    // NO records at all (active or inactive). Only-inactive opens normally with
    // the Inactive tab reachable. Only creators redirect to create.
    if (total === 0 && isDefaultFilters && state.page === 1) {
      const inactiveQs = new URLSearchParams({
        status: "inactive",
        sort: "name",
        dir: "asc",
        page: "1",
      });
      const inactiveRes = await api(meta.api + "?" + inactiveQs.toString());
      if (seq !== requestSeq) return;
      const inactiveTotal =
        inactiveRes.status === 200
          ? listItemsFromBody(inactiveRes.body, kind).total
          : 0;
      const universeEmpty = inactiveTotal === 0;
      if (universeEmpty) {
        if (canCreate) {
          location.hash = meta.createHash;
          return;
        }
        main.replaceChildren();
        const head = h("div");
        head.className = "page-head";
        head.append(h("h1", meta.title));
        main.append(head, emptyCard(`No ${meta.noun} to show.`));
        return;
      }
      // Only inactive records: fall through to normal list frame (empty Active view).
    }

    renderFrame();
    const tbody = main.querySelector("#org-tbody");
    const pager = main.querySelector(".pager");
    const pagerRange = main.querySelector("#pager-range");
    const pagerNav = main.querySelector("#pager-nav");
    const theadRow = main.querySelector("#org-thead");
    if (!tbody || !pager || !pagerRange || !pagerNav || !theadRow) return;

    // Rebuild sort headers with current aria-sort.
    theadRow.replaceChildren();
    const cols = [
      { key: "name", label: "Name" },
      { key: "status", label: "Status", cls: "col-status" },
    ];
    if (meta.parentCol) cols.push({ key: meta.parentSort, label: meta.parentCol, cls: "col-parent" });
    for (const c of cols) {
      const th = document.createElement("th");
      th.scope = "col";
      if (c.cls) th.className = c.cls;
      const sorted = state.sort === c.key;
      th.setAttribute("aria-sort", sorted ? (state.dir === "asc" ? "ascending" : "descending") : "none");
      const btn = document.createElement("button");
      btn.type = "button";
      btn.className = "th-sort";
      btn.append(document.createTextNode(c.label));
      const ind = document.createElement("span");
      ind.innerHTML = sorted ? (state.dir === "asc" ? SORT_UP : SORT_DOWN) : SORT_BOTH;
      btn.append(ind.firstChild);
      btn.addEventListener("click", () => {
        if (state.sort === c.key) state.dir = state.dir === "asc" ? "desc" : "asc";
        else {
          state.sort = c.key;
          state.dir = "asc";
        }
        state.page = 1;
        void loadAndRender();
      });
      th.append(btn);
      theadRow.append(th);
    }
    const chevTh = document.createElement("th");
    chevTh.className = "row-chev";
    chevTh.innerHTML = '<span class="sr-only">Open</span>';
    theadRow.append(chevTh);

    tbody.replaceChildren();
    if (total === 0) {
      const tr = document.createElement("tr");
      tr.className = "no-results";
      const td = document.createElement("td");
      td.colSpan = theadRow.children.length;
      td.textContent = "No results match";
      tr.append(td);
      tbody.append(tr);
      pager.hidden = true;
      pagerRange.textContent = "";
      pagerNav.replaceChildren();
      return;
    }

    pager.hidden = false;
    for (const row of items) {
      const id = row[meta.idKey];
      const tr = document.createElement("tr");
      tr.className = "row-link";
      tr.tabIndex = 0;
      const href = meta.detailHash(id);
      const go = () => {
        location.hash = href;
      };
      tr.addEventListener("click", go);
      tr.addEventListener("keydown", (ev) => {
        if (ev.key === "Enter" || ev.key === " ") {
          ev.preventDefault();
          go();
        }
      });

      const nameTd = document.createElement("td");
      const nameA = h("a", row.name ?? "");
      nameA.href = href;
      nameA.addEventListener("click", (ev) => ev.stopPropagation());
      nameTd.append(nameA);
      tr.append(nameTd);

      const statusTd = document.createElement("td");
      statusTd.append(statusBadge(row.status === "inactive" ? "inactive" : "active"));
      tr.append(statusTd);

      if (meta.parentField) {
        const parentTd = document.createElement("td");
        parentTd.className = "td-parent";
        parentTd.dataset.label = meta.parentCol;
        parentTd.textContent = row[meta.parentField] ?? "";
        tr.append(parentTd);
      }

      const chevTd = document.createElement("td");
      chevTd.className = "row-chev";
      chevTd.setAttribute("aria-hidden", "true");
      chevTd.innerHTML = CHEV;
      tr.append(chevTd);
      tbody.append(tr);
    }

    const pages = Math.max(1, Math.ceil(total / PAGE_SIZE));
    if (state.page > pages) {
      state.page = pages;
      void loadAndRender();
      return;
    }
    const first = (state.page - 1) * PAGE_SIZE + 1;
    const last = Math.min(state.page * PAGE_SIZE, total);
    pagerRange.replaceChildren();
    const b1 = h("b", String(first) + "–" + String(last));
    const b2 = h("b", String(total));
    pagerRange.append(b1, document.createTextNode(" of "), b2, document.createTextNode(" " + meta.noun));

    pagerNav.replaceChildren();
    function addPg(label, opts = {}) {
      const a = document.createElement(opts.disabled ? "span" : "a");
      a.className = "pg" + (opts.num ? " pg-num" : "");
      a.textContent = label;
      if (opts.current) a.setAttribute("aria-current", "page");
      if (opts.disabled) {
        a.setAttribute("aria-disabled", "true");
      } else {
        a.href = "#";
        a.addEventListener("click", (ev) => {
          ev.preventDefault();
          if (opts.page != null) {
            state.page = opts.page;
            void loadAndRender();
          }
        });
      }
      if (opts.aria) a.setAttribute("aria-label", opts.aria);
      pagerNav.append(a);
    }
    addPg("Previous", { disabled: state.page <= 1, page: state.page - 1, aria: "Previous page" });
    const nums = pageNumbers(state.page, pages);
    nums.forEach((p, i) => {
      if (i && p - nums[i - 1] > 1) {
        const el = h("span", "…");
        el.className = "pg-ellipsis";
        el.setAttribute("aria-hidden", "true");
        pagerNav.append(el);
      }
      addPg(String(p), { num: true, current: p === state.page, page: p, aria: "Page " + p });
    });
    addPg("Next", { disabled: state.page >= pages, page: state.page + 1, aria: "Next page" });
  }

  function renderFrame() {
    if (main.querySelector("#org-list-root")) return;
    main.replaceChildren();
    const root = h("div");
    root.id = "org-list-root";

    const pageHead = h("div");
    pageHead.className = "page-head with-action";
    const titles = h("div");
    titles.append(h("h1", meta.title));
    const leadEl = h("p", lead);
    leadEl.className = "page-lead";
    titles.append(leadEl);
    pageHead.append(titles);
    if (canCreate) pageHead.append(primaryLink(meta.createHash, meta.createLabel));
    root.append(pageHead);

    const card = h("div");
    card.className = "list-card";

    const toolbar = h("div");
    toolbar.className = "toolbar";
    toolbar.setAttribute("role", "search");

    const searchField = h("div");
    searchField.className = "field field--search";
    const searchLabel = h("label", "Search by name");
    searchLabel.htmlFor = "org-list-q";
    const wrap = h("div");
    wrap.className = "search-wrap";
    wrap.innerHTML =
      '<svg width="14" height="14" viewBox="0 0 16 16" fill="none" aria-hidden="true">' +
      '<circle cx="7" cy="7" r="5.25" stroke="#9ca3af" stroke-width="1.5"/>' +
      '<path d="M11 11l3.5 3.5" stroke="#9ca3af" stroke-width="1.5" stroke-linecap="round"/></svg>';
    const qInput = document.createElement("input");
    qInput.id = "org-list-q";
    qInput.type = "search";
    qInput.autocomplete = "off";
    qInput.value = state.q;
    qInput.addEventListener("input", () => {
      window.clearTimeout(debounceTimer);
      debounceTimer = window.setTimeout(() => {
        state.q = qInput.value;
        state.page = 1;
        void loadAndRender();
      }, SEARCH_DEBOUNCE_MS);
    });
    wrap.append(qInput);
    searchField.append(searchLabel, wrap);

    const statusField = h("div");
    statusField.className = "field field--status";
    const statusLabel = h("label", "Status");
    statusLabel.htmlFor = "org-list-status";
    const statusSel = document.createElement("select");
    statusSel.id = "org-list-status";
    for (const [val, label] of [
      ["active", "Active"],
      ["inactive", "Inactive"],
    ]) {
      const o = document.createElement("option");
      o.value = val;
      o.textContent = label;
      if (state.status === val) o.selected = true;
      statusSel.append(o);
    }
    statusSel.addEventListener("change", () => {
      state.status = statusSel.value === "inactive" ? "inactive" : "active";
      state.page = 1;
      void loadAndRender();
    });
    statusField.append(statusLabel, statusSel);

    toolbar.append(searchField, statusField);
    card.append(toolbar);

    const table = document.createElement("table");
    table.className = "data";
    table.id = "org-table";
    const cap = document.createElement("caption");
    cap.className = "sr-only";
    cap.textContent = meta.title;
    table.append(cap);
    const thead = document.createElement("thead");
    const tr = document.createElement("tr");
    tr.id = "org-thead";
    thead.append(tr);
    const tbody = document.createElement("tbody");
    tbody.id = "org-tbody";
    table.append(thead, tbody);
    card.append(table);

    const pager = h("div");
    pager.className = "pager";
    const range = h("p");
    range.className = "pager-range";
    range.id = "pager-range";
    range.setAttribute("aria-live", "polite");
    const nav = document.createElement("nav");
    nav.className = "pager-nav";
    nav.id = "pager-nav";
    nav.setAttribute("aria-label", "Pages");
    pager.append(range, nav);
    card.append(pager);

    root.append(card);
    main.append(root);
  }

  renderFrame();
  // Sync inputs if frame already existed from a prior render in this draw.
  const qEl = main.querySelector("#org-list-q");
  const sEl = main.querySelector("#org-list-status");
  if (qEl) qEl.value = state.q;
  if (sEl) sEl.value = state.status;
  await loadAndRender();
}
