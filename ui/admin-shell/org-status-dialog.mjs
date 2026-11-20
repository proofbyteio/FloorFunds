// REQ-068 / DEC-296 / DEC-305 / DEC-307 / DEC-308 / DEC-314 / DEC-317:
// Activate / Deactivate confirm dialogs with APPROVED wording from NOTES.md.

function h(tag, text) {
  const el = document.createElement(tag);
  if (text !== undefined) el.textContent = text;
  return el;
}

function primaryButton(text) {
  const btn = h("button");
  btn.type = "button";
  btn.className = "btn-primary";
  const press = h("span", text);
  press.className = "btn-primary__press";
  btn.append(press);
  return btn;
}

/**
 * @param {{
 *   title: string,
 *   body: string,
 *   confirmLabel: string,
 *   mode: "activate" | "deactivate",
 *   onConfirm: () => Promise<void> | void,
 * }} opts
 */
export function openOrgStatusDialog(opts) {
  const existing = document.getElementById("org-status-dialog");
  existing?.remove();

  const backdrop = h("div");
  backdrop.id = "org-status-dialog";
  backdrop.className = "dialog-backdrop";
  backdrop.setAttribute("role", "presentation");

  const dialog = h("div");
  dialog.className = "dialog";
  dialog.setAttribute("role", "alertdialog");
  dialog.setAttribute("aria-modal", "true");
  dialog.setAttribute("aria-labelledby", "org-status-dialog-title");
  dialog.setAttribute("aria-describedby", "org-status-dialog-desc");

  const title = h("h2", opts.title);
  title.id = "org-status-dialog-title";
  const desc = h("p", opts.body);
  desc.id = "org-status-dialog-desc";

  const actions = h("div");
  actions.className = "dialog-actions";

  const cancel = h("button", "Cancel");
  cancel.type = "button";
  cancel.className = "button button--secondary btn-secondary";

  let confirm;
  if (opts.mode === "activate") {
    confirm = primaryButton(opts.confirmLabel);
  } else {
    confirm = h("button", opts.confirmLabel);
    confirm.type = "button";
    confirm.className = "btn-danger";
  }

  actions.append(cancel, confirm);
  dialog.append(title, desc, actions);
  backdrop.append(dialog);
  document.body.append(backdrop);

  const close = () => backdrop.remove();
  cancel.addEventListener("click", close);
  backdrop.addEventListener("click", (ev) => {
    if (ev.target === backdrop) close();
  });
  confirm.addEventListener("click", async () => {
    confirm.disabled = true;
    cancel.disabled = true;
    try {
      await opts.onConfirm();
      close();
    } catch {
      confirm.disabled = false;
      cancel.disabled = false;
    }
  });
  cancel.focus();
  return { close };
}

/** APPROVED dialog copy builders (NOTES.md DEC-307/308/314/317). */
export function deactivateCopy(kind, name, preview) {
  if (kind === "partner") {
    const clients = Number(preview?.active_clients ?? 0);
    const locations = Number(preview?.active_locations ?? 0);
    return {
      title: `Deactivate ${name}?`,
      body:
        `This also deactivates its ${clients} active clients and ${locations} active locations. ` +
        `Users of ${name} and everything under it won’t be able to sign in until each record is activated again.`,
      confirmLabel: "Deactivate",
      mode: "deactivate",
    };
  }
  if (kind === "client") {
    const locations = Number(preview?.active_locations ?? 0);
    return {
      title: `Deactivate ${name}?`,
      body:
        `This also deactivates its ${locations} active locations. ` +
        `Users of ${name} and everything under it won’t be able to sign in until each record is activated again.`,
      confirmLabel: "Deactivate",
      mode: "deactivate",
    };
  }
  return {
    title: `Deactivate ${name}?`,
    body: `Users of ${name} won’t be able to sign in until it is activated again.`,
    confirmLabel: "Deactivate",
    mode: "deactivate",
  };
}

export function activateCopy(kind, name) {
  if (kind === "partner") {
    return {
      title: `Activate ${name}?`,
      body: `Only ${name} will be active. Its clients and locations stay Inactive until you activate each one.`,
      confirmLabel: "Activate",
      mode: "activate",
    };
  }
  if (kind === "client") {
    return {
      title: `Activate ${name}?`,
      body: `Only ${name} will be active. Its locations stay Inactive until you activate each one.`,
      confirmLabel: "Activate",
      mode: "activate",
    };
  }
  return {
    title: `Activate ${name}?`,
    body: `${name} will show as Active.`,
    confirmLabel: "Activate",
    mode: "activate",
  };
}
