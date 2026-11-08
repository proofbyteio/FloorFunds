/** Browser shell. In-memory only: this file does not read or write web storage. */
import { applyAction, freshState, hrefFor, parseRoute } from "./actions.mjs";
import { renderApp } from "./render.mjs";

const state = freshState("super_admin");
let lastRoute = "";
let restoreId = null;

const app = document.querySelector("#app");

function paint(moveToTitle) {
  const query = document.querySelector("#catalog-query");
  const active = document.activeElement?.id;
  app.innerHTML = renderApp(state);
  const title = document.querySelector("#screen-title");
  document.title = title ? `${title.textContent} — Cash Advance master data` : "Cash Advance master data";
  const restore = restoreId ? document.getElementById(restoreId) : null;
  restoreId = null;
  if (restore) restore.focus();
  else if (moveToTitle && title) title.focus();
  else if (active && document.getElementById(active)) document.getElementById(active).focus();
  if (query && document.querySelector("#catalog-query") && active === "catalog-query") {
    const field = document.querySelector("#catalog-query");
    field.focus();
    const length = field.value.length;
    field.setSelectionRange(length, length);
  }
}

function show(hash, moveToTitle) {
  const route = parseRoute(hash || "#/overview");
  applyAction(state, { type: "navigate", route });
  const key = hrefFor(route);
  const changed = key !== lastRoute;
  lastRoute = key;
  if ((location.hash || "#/overview") !== key) location.hash = key;
  paint(moveToTitle && changed);
}

function readFields() {
  const pairs = [
    ["fee-code", "code"],
    ["fee-name", "displayName"],
    ["fee-description", "description"],
    ["reason", "reason"],
    ["partner-text", "partnerText"],
    ["location-text", "locationText"],
  ];
  for (const [id, name] of pairs) {
    const field = document.getElementById(id);
    if (field) applyAction(state, { type: "set-field", name, value: field.value });
  }
}

window.addEventListener("hashchange", () => show(location.hash, true));

app.addEventListener("change", (event) => {
  const target = event.target;
  if (!(target instanceof HTMLElement)) return;
  if (target.id === "actor-role" && target instanceof HTMLSelectElement) {
    applyAction(state, { type: "set-role", role: target.value });
    restoreId = "actor-role";
    paint(false);
  } else if (target.id === "scope-filter" && target instanceof HTMLSelectElement) {
    applyAction(state, { type: "set-scope", scope: target.value });
    restoreId = "scope-filter";
    paint(false);
  } else if (target.id === "fixture-toggle" && target instanceof HTMLInputElement) {
    applyAction(state, { type: "toggle-fixture", on: target.checked });
    restoreId = "fixture-toggle";
    paint(false);
  }
});

app.addEventListener("submit", (event) => {
  event.preventDefault();
  const check = event.target.querySelector("[data-action]");
  if (check instanceof HTMLElement) check.click();
});

app.addEventListener("click", (event) => {
  const target = event.target;
  if (!(target instanceof Element)) return;
  const control = target.closest("[data-action]");
  if (!control) return;
  const action = control.dataset.action;
  readFields();
  if (action === "search") {
    const field = document.querySelector("#catalog-query");
    applyAction(state, { type: "set-query", query: field ? field.value : "" });
    restoreId = "search-button";
    paint(false);
    return;
  }
  if (action === "clear-filters") {
    applyAction(state, { type: "clear-filters" });
    restoreId = "clear-filters";
    paint(false);
    return;
  }
  if (action === "check-activation") {
    applyAction(state, {
      type: "check-activation",
      mode: control.dataset.mode,
      desiredActive: control.dataset.desired === "true",
    });
    restoreId = "check-activation";
    paint(false);
    return;
  }
  if (action === "submit-fee" || action === "replay-fee" || action === "try-claim-edit" || action === "try-metadata" || action === "try-closed-create") {
    applyAction(state, { type: action === "submit-fee" ? "submit-fee" : action === "replay-fee" ? "replay-fee" : action === "try-claim-edit" ? "try-claim-edit" : action === "try-metadata" ? "try-metadata" : "try-closed-create" });
    restoreId = control.id || null;
    paint(false);
  }
});

show(location.hash || "#/overview", false);
