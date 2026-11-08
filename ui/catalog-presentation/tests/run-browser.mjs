/** Headless Chrome audit: labels, Tab focus, clicks, and viewport screenshots. Not a screen reader. */
import { spawn } from "node:child_process";
import { mkdirSync, writeFileSync } from "node:fs";

const base = process.env.CATALOG_URL || "http://127.0.0.1:8765/ui/catalog-presentation/index.html";
const outDir = process.env.ARTIFACT_DIR || "/opt/cursor/artifacts";
mkdirSync(outDir, { recursive: true });

const chrome = spawn("google-chrome", [
  "--headless=new",
  "--no-sandbox",
  "--disable-gpu",
  "--disable-dev-shm-usage",
  "--user-data-dir=/tmp/chrome-catalog-profile",
  "--remote-debugging-port=9333",
  "--window-size=1600,1000",
  "about:blank",
], { stdio: "ignore" });

const sleep = (ms) => new Promise((resolve) => setTimeout(resolve, ms));

async function json(path) {
  const response = await fetch(`http://127.0.0.1:9333${path}`);
  return response.json();
}

let seq = 0;
const pending = new Map();
let socket;

let sessionId = null;

function send(method, params = {}) {
  const id = ++seq;
  const message = { id, method, params };
  if (sessionId) message.sessionId = sessionId;
  socket.send(JSON.stringify(message));
  return new Promise((resolve, reject) => {
    pending.set(id, { resolve, reject });
  });
}

function connect(url) {
  socket = new WebSocket(url);
  socket.addEventListener("message", (event) => {
    const message = JSON.parse(event.data);
    if (message.id && pending.has(message.id)) {
      const waiter = pending.get(message.id);
      pending.delete(message.id);
      if (message.error) waiter.reject(new Error(JSON.stringify(message.error)));
      else waiter.resolve(message.result);
    }
  });
  return new Promise((resolve, reject) => {
    socket.addEventListener("open", resolve);
    socket.addEventListener("error", reject);
  });
}

async function evaluate(expression) {
  const result = await send("Runtime.evaluate", { expression, returnByValue: true, awaitPromise: true });
  if (result.exceptionDetails) throw new Error(JSON.stringify(result.exceptionDetails));
  return result.result.value;
}

async function navigate(hash) {
  await send("Page.navigate", { url: `${base}${hash}` });
  const marker = hash.replace(/^#/, "");
  for (let attempt = 0; attempt < 40; attempt++) {
    await sleep(100);
    try {
      const ready = await evaluate(`location.hash.includes(${JSON.stringify(marker)}) && !!document.getElementById("screen-title")`);
      if (ready) return await evaluate("document.getElementById('screen-title').textContent");
    } catch {
      // The page context is replaced while navigation finishes.
    }
  }
  throw new Error(`No screen title for ${hash}`);
}

async function shot(name) {
  const image = await send("Page.captureScreenshot", { format: "png", captureBeyondViewport: false });
  const path = `${outDir}/${name}.png`;
  writeFileSync(path, Buffer.from(image.data, "base64"));
  return path;
}

async function resize(width, height) {
  await send("Emulation.setDeviceMetricsOverride", { width, height, deviceScaleFactor: 1, mobile: width < 900 });
  await sleep(150);
}

const report = { passes: [], failures: [], focus: [], shots: [], limits: [] };

function check(name, ok, detail) {
  (ok ? report.passes : report.failures).push({ name, detail });
}

try {
  for (let i = 0; i < 40; i++) {
    try {
      await json("/json/version");
      break;
    } catch {
      await sleep(100);
    }
  }
  const version = await json("/json/version");
  await connect(version.webSocketDebuggerUrl);
  const created = await send("Target.createTarget", { url: "about:blank" });
  const attached = await send("Target.attachToTarget", { targetId: created.targetId, flatten: true });
  sessionId = attached.sessionId;
  await send("Page.enable");
  await send("Runtime.enable");
  await resize(1600, 1000);
  await navigate("#/overview");
  check("overview title", (await evaluate("document.getElementById('screen-title').textContent")).includes("Catalog overview"));
  report.shots.push(await shot("overview-1600"));

  const unlabeled = await evaluate(`(() => {
    const problems = [];
    for (const el of document.querySelectorAll("input, select, textarea")) {
      const label = el.id && document.querySelector('label[for="' + el.id + '"]');
      const named = label || el.getAttribute("aria-label") || el.getAttribute("aria-labelledby");
      if (!named) problems.push(el.id || el.name || el.outerHTML.slice(0, 80));
    }
    return problems;
  })()`);
  check("overview controls have labels", unlabeled.length === 0, unlabeled.join(","));

  const storageBefore = await evaluate("localStorage.length + ':' + sessionStorage.length");
  await send("Runtime.evaluate", { expression: "document.querySelector('.skip-link').focus()" });
  const focus = [];
  for (let i = 0; i < 12; i++) {
    await send("Input.dispatchKeyEvent", { type: "keyDown", key: "Tab", code: "Tab", windowsVirtualKeyCode: 9 });
    await send("Input.dispatchKeyEvent", { type: "keyUp", key: "Tab", code: "Tab", windowsVirtualKeyCode: 9 });
    focus.push(await evaluate(`(() => {
      const el = document.activeElement;
      const label = el.id ? document.querySelector('label[for="' + el.id + '"]') : null;
      return {
        tag: el.tagName,
        id: el.id || "",
        text: (label ? label.textContent : el.getAttribute("aria-label") || el.textContent || "").trim().slice(0, 80),
      };
    })()`));
  }
  report.focus = focus;
  check("tab moves focus", new Set(focus.map((item) => item.id + item.text)).size > 3, JSON.stringify(focus));
  check("focused controls have names", focus.every((item) => item.text.length > 0), JSON.stringify(focus));

  await navigate("#/family/transaction_type/list");
  report.shots.push(await shot("transaction-type-list-1600"));
  await resize(1280, 900);
  report.shots.push(await shot("transaction-type-list-1280"));
  await resize(768, 1000);
  report.shots.push(await shot("transaction-type-list-768"));
  await resize(1600, 1000);

  await navigate("#/family/card_network/list");
  await evaluate("document.querySelector('.note-gap')?.scrollIntoView({block:'center'})");
  report.shots.push(await shot("card-network-list-1600"));
  await navigate("#/family/currency/list");
  await evaluate("document.querySelector('.note-gap')?.scrollIntoView({block:'center'})");
  report.shots.push(await shot("currency-list-1600"));
  await navigate("#/family/id_document_type/list");
  report.shots.push(await shot("id-document-list-1600"));

  await navigate("#/shared/empty");
  const emptyText = await evaluate("document.body.innerText");
  check("empty state", emptyText.includes("No matching results"), emptyText.slice(0, 180));
  report.shots.push(await shot("empty-results-1600"));

  await navigate("#/shared/same-code");
  const sameText = await evaluate("document.body.innerText");
  check("same code both identities", sameText.includes("EXAMPLE-5-A") && sameText.includes("11000000-0000-4000-8005-100000000001") && sameText.includes("11000000-0000-4000-8005-200000000001") && sameText.includes("same as a missing row") && sameText.includes("Super Admin"));
  await evaluate("document.querySelector('.results-card')?.scrollIntoView({block:'center'})");
  report.shots.push(await shot("same-code-1600"));

  await navigate("#/shared/graph");
  check("unknown graph", (await evaluate("document.body.innerText")).includes("UNCONFIGURED"));
  report.shots.push(await shot("graph-1600"));

  await navigate("#/family/model_hardware_claim/list");
  const editHref = await evaluate(`(() => {
    const links = [...document.querySelectorAll("a")].filter((item) => item.textContent.trim() === "Edit").map((item) => item.getAttribute("href"));
    return {
      hash: location.hash,
      title: document.getElementById("screen-title")?.textContent || "",
      anchors: document.querySelectorAll("a").length,
      rows: document.querySelectorAll("tbody tr").length,
      sample: [...document.querySelectorAll("main a")].slice(0, 8).map((item) => item.textContent.trim()),
      links,
    };
  })()`);
  report.claimList = editHref;
  if (!editHref.links?.length) throw new Error("No claim edit link: " + JSON.stringify(editHref));
  await navigate(editHref.links[0]);
  const editText = await evaluate("document.body.innerText");
  check("same-id edit", editText.includes("Ordinary same-ID edit") && !editText.includes("Supersede / Correct"), editText.slice(0, 240));
  check("no variant field", await evaluate('document.querySelector("[name=variant_note]") === null'));
  await evaluate("document.getElementById('try-claim-edit').click()");
  await sleep(200);
  const claimResult = await evaluate("document.body.innerText");
  check("claim edit writes nothing", claimResult.includes("Wrote") && claimResult.includes("No") && claimResult.includes("503"));
  const claimField = await evaluate(`(() => {
    const el = document.getElementById("claim-availability");
    const style = getComputedStyle(el);
    return { disabled: el.disabled, bg: style.backgroundColor, color: style.color };
  })()`);
  check("disabled claim fields are muted", claimField.disabled && claimField.bg === "rgb(243, 245, 248)" && claimField.color === "rgb(91, 100, 114)", JSON.stringify(claimField));
  await evaluate("document.getElementById('claim-availability')?.scrollIntoView({block:'center'})");
  report.shots.push(await shot("claim-edit-refused-1600"));
  await resize(768, 1000);
  report.shots.push(await shot("claim-edit-refused-768"));
  await resize(1600, 1000);

  await navigate("#/family/fee_type/add");
  check("fee form omits economic role control", await evaluate('document.querySelector("[name=economic_role]") === null'));
  await evaluate(`(() => {
    document.getElementById("fee-code").value = "SESSION";
    document.getElementById("fee-name").value = "TEST-ONLY browser fee";
    document.getElementById("fee-description").value = "Synthetic session row";
    document.getElementById("fee-save").click();
  })()`);
  await sleep(200);
  const feeText = await evaluate("document.body.innerText");
  check("fee session create", feeText.includes("201") && feeText.includes("Synthetic session only") && feeText.includes("not claimed"));
  report.shots.push(await shot("fee-add-session-1600"));
  const feeId = await evaluate(`document.body.innerText.includes("40000000-0000-4000-8000")`);
  check("fee id visible or result present", feeText.includes("created") || feeId);

  await navigate("#/family/transaction_type/list");
  const offLink = await evaluate(`(() => {
    const link = [...document.querySelectorAll("a")].find((item) => item.textContent.trim() === "Turn Off");
    return link.getAttribute("href");
  })()`);
  await navigate(offLink);
  await evaluate(`(() => { document.getElementById("reason").value = "browser check"; document.getElementById("check-activation").click(); })()`);
  await sleep(200);
  const offText = await evaluate("document.body.innerText");
  check("off is 503 unknown impact", offText.includes("503") && offText.includes("Unavailable. Not zero.") && offText.includes("Wrote") && !offText.includes("0 linked"));
  check("off wrote no", /Wrote\s*No/.test(offText));
  await evaluate("document.querySelector('.outcome')?.scrollIntoView({block:'center'})");
  report.shots.push(await shot("off-unavailable-1600"));
  await resize(1280, 900);
  report.shots.push(await shot("off-unavailable-1280"));
  await resize(1600, 1000);

  await navigate("#/family/catalog_release/add");
  const closedField = await evaluate(`(() => {
    const el = document.getElementById("closed-code");
    const style = getComputedStyle(el);
    return { disabled: el.disabled, bg: style.backgroundColor };
  })()`);
  check("closed create fields look disabled", closedField.disabled && closedField.bg === "rgb(243, 245, 248)", JSON.stringify(closedField));
  await evaluate("document.getElementById('closed-code')?.scrollIntoView({block:'center'})");
  report.shots.push(await shot("closed-create-disabled-1600"));
  await evaluate("document.getElementById('closed-create').click()");
  await sleep(150);
  const releaseText = await evaluate("document.body.innerText");
  check("release create unavailable", releaseText.includes("required_fields_unresolved") && /Wrote\s*No/.test(releaseText));

  await navigate("#/family/model_hardware_claim/list");
  const claimOnLink = await evaluate(`(() => {
    const link = [...document.querySelectorAll("a")].find((item) => item.textContent.trim() === "Turn On");
    return link ? link.getAttribute("href") : "";
  })()`);
  await navigate(claimOnLink);
  const banner = await evaluate("document.body.innerText");
  check("claim on banner for all actors", banner.includes("PQ-272") && banner.includes("including Super Admin"));
  await evaluate(`(() => { document.getElementById("reason").value = "reactivate"; document.getElementById("check-activation").click(); })()`);
  await sleep(200);
  const onText = await evaluate("document.body.innerText");
  check("claim on 503", onText.includes("503") && onText.includes("pending_pq272") && /Wrote\s*No/.test(onText));
  await evaluate("document.querySelector('.outcome')?.scrollIntoView({block:'center'})");
  report.shots.push(await shot("claim-on-unavailable-1600"));

  const storageAfter = await evaluate("localStorage.length + ':' + sessionStorage.length");
  check("web storage unchanged", storageBefore === storageAfter, storageBefore + " -> " + storageAfter);

  await navigate("#/family/transaction_type/list");
  const cashier = await evaluate(`(() => {
    document.getElementById("actor-role").value = "cashier";
    document.getElementById("actor-role").dispatchEvent(new Event("change", { bubbles: true }));
    return "set";
  })()`);
  await sleep(200);
  const cashierText = await evaluate("document.body.innerText");
  check("cashier list forbidden", cashierText.includes("no_management_grant") && cashierText.includes("403"));
  report.shots.push(await shot("cashier-forbidden-1600"));
  check("cashier dispatch", cashier === "set");
} catch (error) {
  report.failures.push({ name: "browser harness", detail: String(error && error.stack || error) });
} finally {
  report.limits = [
    "Headless Chrome only. No screen reader, no zoom session, and no physical keyboard.",
    "Contrast figures in the node test use the stylesheet tokens, not a pixel sample of every state.",
    "Tab order is the first 12 Tab presses from the skip link on the overview.",
    "Screenshots are the viewport, not a full-page capture of every route.",
  ];
  writeFileSync(`${outDir}/browser-audit.json`, JSON.stringify(report, null, 2));
  chrome.kill();
  socket?.close();
  console.log(JSON.stringify({ passes: report.passes.length, failures: report.failures, shots: report.shots, focus: report.focus }, null, 2));
  process.exit(report.failures.length === 0 ? 0 : 1);
}
