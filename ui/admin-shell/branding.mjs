// DEC-228 / DEC-230. The lockup name is the configured product name.
// The text node is the full string. CSS truncates it; this function does not.

export function productNameFromConfig(source) {
  if (source && typeof source.product_name === "string") return source.product_name;
  if (source && source.Branding && typeof source.Branding.ProductName === "string") return source.Branding.ProductName;
  return "";
}

export function applyProductName(nameEl, name) {
  if (!nameEl) return;
  const value = typeof name === "string" ? name : "";
  nameEl.textContent = value;
  if (value) nameEl.setAttribute("title", value);
  else nameEl.removeAttribute("title");
}
