// DEC-334 / PQ-365: Location Admin home picker (Playerhub-style dialog, DEC-227).
// PM defaults (DEC-241): (a) one location → no popup; (b) ACTIVE + server-scoped list;
// (d) reopen to switch; (e) reuse dialog-backdrop / dialog chrome.

function h(tag, text) {
  const el = document.createElement(tag);
  if (text !== undefined) el.textContent = text;
  return el;
}

/**
 * @param {{
 *   locations: { location_id: string, name: string }[],
 *   onPick: (locationId: string) => void,
 *   allowDismiss?: boolean,
 * }} opts
 */
export function openLaLocationPicker(opts) {
  const existing = document.getElementById("la-location-picker");
  existing?.remove();

  const backdrop = h("div");
  backdrop.id = "la-location-picker";
  backdrop.className = "dialog-backdrop";
  backdrop.setAttribute("role", "presentation");

  const dialog = h("div");
  dialog.className = "dialog";
  dialog.setAttribute("role", "dialog");
  dialog.setAttribute("aria-modal", "true");
  dialog.setAttribute("aria-labelledby", "la-location-picker-title");
  dialog.setAttribute("aria-describedby", "la-location-picker-desc");

  const title = h("h2", "Choose a location");
  title.id = "la-location-picker-title";
  const desc = h("p", "Select which location you are working in now.");
  desc.id = "la-location-picker-desc";

  const list = h("ul");
  list.className = "la-picker-list";
  list.setAttribute("role", "listbox");
  list.setAttribute("aria-labelledby", "la-location-picker-title");

  const close = () => backdrop.remove();

  for (const row of opts.locations) {
    const id = row.location_id;
    const name = row.name || id;
    const li = h("li");
    const btn = h("button", name);
    btn.type = "button";
    btn.className = "la-picker-item";
    btn.setAttribute("role", "option");
    btn.addEventListener("click", () => {
      close();
      opts.onPick(id);
    });
    li.append(btn);
    list.append(li);
  }

  dialog.append(title, desc, list);

  if (opts.allowDismiss) {
    const actions = h("div");
    actions.className = "dialog-actions";
    const cancel = h("button", "Cancel");
    cancel.type = "button";
    cancel.className = "button button--secondary btn-secondary";
    cancel.addEventListener("click", close);
    actions.append(cancel);
    dialog.append(actions);
    backdrop.addEventListener("click", (ev) => {
      if (ev.target === backdrop) close();
    });
  }

  backdrop.append(dialog);
  document.body.append(backdrop);
  list.querySelector("button")?.focus();
  return { close };
}
