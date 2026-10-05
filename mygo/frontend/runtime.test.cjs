const assert = require("node:assert/strict");
const { readFileSync } = require("node:fs");
const path = require("node:path");
const { test } = require("node:test");
const vm = require("node:vm");
const ts = require("../../desktop/node_modules/typescript");

function createRuntime(reply = { success: true, data: 42 }) {
  const calls = [];
  const listeners = new Map();
  const fetched = [];
  const fetchOptions = [];
  class Element {
    constructor() {
      this.attributes = {};
    }
    setAttribute(name, value) {
      this.attributes[name] = value;
    }
  }
  class Image extends Element {}
  class Source extends Element {}
  class Anchor extends Element {}
  for (const [prototype, property] of [
    [Image.prototype, "src"],
    [Source.prototype, "src"],
    [Anchor.prototype, "href"],
  ]) {
    Object.defineProperty(prototype, property, {
      configurable: true,
      get() {
        return this.attributes[property];
      },
      set(value) {
        this.attributes[property] = value;
      },
    });
  }
  class Style {
    setProperty(property, value) {
      this[property] = value;
    }
  }
  for (const property of [
    "cssText",
    "background",
    "backgroundImage",
    "maskImage",
    "webkitMaskImage",
  ]) {
    Object.defineProperty(Style.prototype, property, {
      configurable: true,
      get() {
        return this[`_${property}`];
      },
      set(value) {
        this[`_${property}`] = value;
      },
    });
  }
  class Xhr {
    open(method, url) {
      this.method = method;
      this.url = url;
    }
  }
  const window = {
    Request,
    fetch: async (input, init) => {
      fetched.push(input);
      fetchOptions.push(init);
      return new Response("ok");
    },
    akariWindowType: "aux-window",
    mygo: {
      call: async (...args) => {
        calls.push(args);
        return reply;
      },
      on: (event, listener) => {
        listeners.set(event, listener);
        return () => listeners.delete(event);
      },
    },
  };
  const context = vm.createContext({
    window,
    exports: {},
    URL,
    Request,
    Response,
    XMLHttpRequest: Xhr,
    Element,
    HTMLImageElement: Image,
    HTMLSourceElement: Source,
    HTMLAnchorElement: Anchor,
    CSSStyleDeclaration: Style,
    document: { documentElement: { dataset: {} } },
  });
  const source = readFileSync(path.join(__dirname, "runtime.ts"), "utf8");
  const code = ts.transpileModule(source, {
    compilerOptions: {
      module: ts.ModuleKind.CommonJS,
      target: ts.ScriptTarget.ES2022,
    },
  }).outputText;
  vm.runInContext(code, context);
  return { window, calls, listeners, fetched, fetchOptions, Image, Style, Xhr };
}

test("buffers native Request bodies for the Windows WebView interceptor while preserving cancellation", async () => {
  const runtime = createRuntime();
  const controller = new AbortController();
  const original = new Request(
    "akari://sgp/challenges-client/v2/all-player-data/",
    {
      method: "POST",
      headers: {
        "content-type": "application/json",
        "x-akari-token-type": "league-session",
      },
      body: "[]",
      signal: controller.signal,
    },
  );
  await runtime.window.fetch(original);
  assert.equal(
    runtime.fetched[0],
    "http://akari.localhost/sgp/challenges-client/v2/all-player-data/",
  );
  const options = runtime.fetchOptions[0];
  assert.equal(options.method, "POST");
  assert.equal(Buffer.from(options.body).toString(), "[]");
  assert.equal(options.headers.get("x-akari-token-type"), "league-session");
  controller.abort();
  assert.equal(options.signal.aborted, true);
});

test("forwards the existing IPC envelope and events with a working unsubscribe", async () => {
  const runtime = createRuntime();
  const result = await runtime.window.electron.ipcRenderer.invoke(
    "akariCall",
    "league-client-main",
    "connect",
    { pid: 17 },
  );
  assert.deepEqual(runtime.calls[0].slice(0, 3), [
    "Desktop.Call",
    "league-client-main",
    "connect",
  ]);
  assert.equal(runtime.calls[0][3][0].pid, 17);
  assert.equal(result.data, 42);
  assert.equal(runtime.window.akariWindowType, "aux-window");
  await runtime.window.electron.ipcRenderer.invoke(
    "akariRendererRegister",
    "register",
  );
  assert.equal(runtime.calls.length, 1);

  let received;
  const off = runtime.window.electron.ipcRenderer.on(
    "akari-event",
    (...args) => {
      received = args;
    },
  );
  runtime.listeners.get("akari-event")({
    namespace: "mobx-utils-main",
    name: "update-state-prop/league-client-main:gameflow",
    args: ["phase", "ChampSelect", { action: "update", raw: false }],
  });
  assert.equal(received[1], "mobx-utils-main");
  assert.equal(received[3], "phase");
  assert.equal(received[4], "ChampSelect");
  off();
  assert.equal(runtime.listeners.has("akari-event"), false);
});

test("keeps domain, query, request body and external URLs at the WebView proxy boundary", async () => {
  const runtime = createRuntime();
  const request = new runtime.window.Request(
    new URL("/lol-champ-select/v1/session?x=1", "akari://league-client"),
    { method: "PATCH", body: '{"id":22}' },
  );
  assert.equal(
    request.url,
    "http://akari.localhost/league-client/lol-champ-select/v1/session?x=1",
  );
  assert.equal(await request.text(), '{"id":22}');
  await runtime.window.fetch(
    "akari://sgp/match-history-query/v1/products/lol/player/p/SUMMARY",
  );
  assert.equal(
    runtime.fetched[0],
    "http://akari.localhost/sgp/match-history-query/v1/products/lol/player/p/SUMMARY",
  );
  await runtime.window.fetch("https://example.com/a");
  assert.equal(runtime.fetched[1], "https://example.com/a");
  const xhr = new runtime.Xhr();
  xhr.open(
    "GET",
    "akari://riot-client/player-account/lookup/v1/namesets-for-puuids",
  );
  assert.equal(
    xhr.url,
    "http://akari.localhost/riot-client/player-account/lookup/v1/namesets-for-puuids",
  );
});

test("rewrites champion images and skin CSS URLs before browser resource loading", () => {
  const runtime = createRuntime();
  const image = new runtime.Image();
  image.src =
    "akari://league-client/lol-game-data/assets/v1/champion-icons/103.png";
  assert.equal(
    image.src,
    "http://akari.localhost/league-client/lol-game-data/assets/v1/champion-icons/103.png",
  );
  image.setAttribute("src", "akari://league-client/a.png");
  assert.equal(image.src, "http://akari.localhost/league-client/a.png");
  const style = new runtime.Style();
  style.backgroundImage = 'url("akari://league-client/skin.jpg")';
  assert.equal(
    style.backgroundImage,
    'url("http://akari.localhost/league-client/skin.jpg")',
  );
  style.setProperty("mask-image", "url(akari://league-client/perk.svg)");
  assert.equal(
    style["mask-image"],
    "url(http://akari.localhost/league-client/perk.svg)",
  );
});

test("preserves browser string coercion for boolean and numeric element attributes", () => {
  const runtime = createRuntime();
  const element = new runtime.Image();
  element.setAttribute("draggable", false);
  element.setAttribute("aria-hidden", true);
  element.setAttribute("width", 48);
  assert.equal(element.attributes.draggable, "false");
  assert.equal(element.attributes["aria-hidden"], "true");
  assert.equal(element.attributes.width, "48");
});

test("loads absolute local file images through the WebView resource proxy", () => {
  const runtime = createRuntime();
  const image = new runtime.Image();
  image.src = "file:///C:/本地图片/英雄.png";
  assert.equal(
    image.src,
    "http://akari.localhost/local-image?url=" +
      encodeURIComponent("file:///C:/本地图片/英雄.png"),
  );
  const style = new runtime.Style();
  style.backgroundImage = 'url("file:///C:/images/skin.jpg")';
  assert.equal(
    style.backgroundImage,
    'url("http://akari.localhost/local-image?url=' +
      encodeURIComponent("file:///C:/images/skin.jpg") +
      '")',
  );
});

test("restores selectable champion sets from Go initial state and later client events", async () => {
  const reply = {
    success: true,
    data: {
      currentPickableChampionIds: { value: [103, 127], config: { raw: true } },
      currentBannableChampionIds: { value: [22], config: { raw: true } },
      disabledChampionIds: { value: [711], config: { raw: true } },
      session: { value: { myTeam: [] }, config: { raw: true } },
    },
  };
  const runtime = createRuntime(reply);
  const result = await runtime.window.electron.ipcRenderer.invoke(
    "akariCall",
    "mobx-utils-main",
    "subscribeAndGetInitialState",
    "league-client-main",
    "champSelect",
  );
  assert.equal(result.data.currentPickableChampionIds.value.has(103), true);
  assert.equal(result.data.currentBannableChampionIds.value.has(22), true);
  assert.equal(result.data.disabledChampionIds.value.has(711), true);
  assert.equal(Array.isArray(result.data.session.value.myTeam), true);

  let received;
  runtime.window.electron.ipcRenderer.on("akari-event", (...args) => {
    received = args;
  });
  runtime.listeners.get("akari-event")({
    namespace: "mobx-utils-main",
    name: "update-state-prop/league-client-main:champSelect",
    args: [
      "currentPickableChampionIds",
      [1, 2],
      { action: "update", raw: true },
    ],
  });
  assert.equal(received[4].has(1), true);
  assert.equal(received[4].has(103), false);
  runtime.listeners.get("akari-event")({
    namespace: "mobx-utils-main",
    name: "update-state-prop/another-main:champSelect",
    args: [
      "currentPickableChampionIds",
      [1, 2],
      { action: "update", raw: true },
    ],
  });
  assert.equal(Array.isArray(received[4]), true);
});
