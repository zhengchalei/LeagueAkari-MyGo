interface AkariEventPayload {
  namespace: string;
  name: string;
  args: unknown[];
}

interface MyGoRuntime {
  call<T = unknown>(method: string, ...args: unknown[]): Promise<T>;
  on<T = unknown>(event: string, listener: (payload: T) => void): () => void;
  platform: "win32" | "darwin" | "linux";
}

interface DesktopCallResult {
  success: boolean;
  data?: unknown;
  error?: unknown;
}

declare global {
  interface Window {
    mygo: MyGoRuntime;
  }
}

const AKARI_WEBVIEW_ORIGIN = "http://akari.localhost";

export function toWebViewUrl(source: string): string {
  if (source.startsWith("file:")) {
    return `${AKARI_WEBVIEW_ORIGIN}/local-image?url=${encodeURIComponent(source)}`;
  }
  return source.startsWith("akari://")
    ? `${AKARI_WEBVIEW_ORIGIN}/${source.slice("akari://".length)}`
    : source;
}

function toWebViewStyle(source: string): string {
  return source
    .replaceAll("akari://", `${AKARI_WEBVIEW_ORIGIN}/`)
    .replace(
      /url\(\s*(["']?)(file:[^"')]+)\1\s*\)/g,
      (_match, _quote, url: string) => `url("${toWebViewUrl(url)}")`,
    );
}

const CHAMP_SELECT_SET_FIELDS = new Set([
  "currentPickableChampionIds",
  "currentBannableChampionIds",
  "disabledChampionIds",
]);

function restoreChampSelectSet(path: string, value: unknown): unknown {
  if (CHAMP_SELECT_SET_FIELDS.has(path) && Array.isArray(value)) {
    return new Set(value);
  }
  return value;
}

// Keep the existing shard IPC contract while Go owns the client connection and storage.
const ipcRenderer = {
  async invoke(channel: string, ...args: unknown[]) {
    if (channel === "akariRendererRegister") {
      return;
    }
    if (channel !== "akariCall") {
      throw new Error(`Unsupported desktop IPC channel: ${channel}`);
    }
    const [namespace, method, ...methodArgs] = args;
    if (
      namespace === "in-game-send-main" &&
      /^(generate|send)(Rating|Jungle|Premade)Preset(?:Lines)?$/.test(
        String(method),
      )
    ) {
      try {
        const match = String(method).match(
          /^(generate|send)(Rating|Jungle|Premade)/,
        )!;
        const { generatePresetLines } = await import("./send-presets");
        const target = methodArgs[0] as "friendly" | "enemy" | "all";
        const lines = generatePresetLines(match[2], target);
        if (match[1] === "generate") return { success: true, data: lines };
        return window.mygo.call<DesktopCallResult>(
          "Desktop.Call",
          namespace,
          "sendLines",
          [lines],
        );
      } catch (error) {
        return {
          success: false,
          error: {
            message: error instanceof Error ? error.message : String(error),
          },
        };
      }
    }
    const result = await window.mygo.call<DesktopCallResult>(
      "Desktop.Call",
      namespace,
      method,
      methodArgs,
    );
    if (
      result.success &&
      namespace === "mobx-utils-main" &&
      method === "subscribeAndGetInitialState" &&
      methodArgs[0] === "league-client-main" &&
      methodArgs[1] === "champSelect"
    ) {
      const data = result.data as Record<
        string,
        { value: unknown; config: unknown }
      >;
      for (const field of CHAMP_SELECT_SET_FIELDS) {
        if (data[field])
          data[field].value = restoreChampSelectSet(field, data[field].value);
      }
    }
    return result;
  },
  on(channel: string, callback: (...args: unknown[]) => void) {
    if (channel !== "akari-event") {
      throw new Error(`Unsupported desktop event channel: ${channel}`);
    }
    return window.mygo.on<AkariEventPayload>("akari-event", (payload) => {
      const args = payload.args ?? [];
      if (
        payload.namespace === "mobx-utils-main" &&
        payload.name === "update-state-prop/league-client-main:champSelect"
      ) {
        args[1] = restoreChampSelectSet(String(args[0]), args[1]);
      }
      callback({}, payload.namespace, payload.name, ...args);
    });
  },
};

// The compatibility surface is deliberately limited to the two renderer IPC operations.
window.electron = { ipcRenderer } as typeof window.electron;
window.akariWindowType ||=
  document.documentElement.dataset.windowType || "main-window";
if (window.akariWindowType === "main-window") {
  window.mygo.on<AkariEventPayload>("akari-event", (event) => {
    if (
      event.namespace !== "in-game-send-main" ||
      event.name !== "request-preset"
    )
      return;
    const [kind, target] = event.args;
    void ipcRenderer.invoke(
      "akariCall",
      "in-game-send-main",
      `send${kind}Preset`,
      target,
    );
  });
}

// Rewrite at the browser boundary: URL('/path', 'akari://league-client') must keep
// the original host until after resolution, or the proxy domain is lost.
const NativeRequest = window.Request;
window.Request = class WebViewRequest extends NativeRequest {
  constructor(input: RequestInfo | URL, init?: RequestInit) {
    if (typeof input === "string") {
      super(toWebViewUrl(input), init);
    } else if (input instanceof URL) {
      super(toWebViewUrl(input.href), init);
    } else if (input.url.startsWith("akari://")) {
      super(new NativeRequest(toWebViewUrl(input.url), input), init);
    } else {
      super(input, init);
    }
  }
};

const nativeFetch = window.fetch.bind(window);
async function fetchWebViewRequest(
  input: Request,
  init?: RequestInit,
): Promise<Response> {
  const request = new window.Request(input, init);
  if (!request.body) return nativeFetch(request);
  // Axios can retain the original Request constructor. Copying its body to a new
  // URL produces a stream that WebView2's native interceptor cannot read yet.
  const body = await request.arrayBuffer();
  return nativeFetch(request.url, {
    method: request.method,
    headers: request.headers,
    body,
    signal: request.signal,
    credentials: request.credentials,
    cache: request.cache,
    mode: request.mode,
    redirect: request.redirect,
    referrer: request.referrer,
    referrerPolicy: request.referrerPolicy,
    integrity: request.integrity,
    keepalive: request.keepalive,
  });
}
window.fetch = (input, init) => {
  if (typeof input === "string") {
    return nativeFetch(toWebViewUrl(input), init);
  }
  if (input instanceof URL) {
    return nativeFetch(toWebViewUrl(input.href), init);
  }
  if (
    input.url.startsWith("akari://") ||
    input.url.startsWith(`${AKARI_WEBVIEW_ORIGIN}/`)
  ) {
    return fetchWebViewRequest(input, init);
  }
  return nativeFetch(input, init);
};

const nativeOpen = XMLHttpRequest.prototype.open;
XMLHttpRequest.prototype.open = function (
  this: XMLHttpRequest,
  method,
  url,
  ...args
) {
  return Reflect.apply(nativeOpen, this, [
    method,
    toWebViewUrl(String(url)),
    ...args,
  ]);
} as typeof XMLHttpRequest.prototype.open;

const nativeSetAttribute = Element.prototype.setAttribute;
Element.prototype.setAttribute = function (name, value) {
  const text = String(value);
  const normalizedValue =
    name === "style" ? toWebViewStyle(text) : toWebViewUrl(text);
  nativeSetAttribute.call(this, name, normalizedValue);
};

function rewriteProperty(
  prototype: object,
  property: string,
  rewrite: (value: string) => string,
) {
  const descriptor = Object.getOwnPropertyDescriptor(prototype, property);
  if (!descriptor?.set || !descriptor.get) {
    return;
  }
  Object.defineProperty(prototype, property, {
    ...descriptor,
    set(value: string) {
      descriptor.set!.call(this, rewrite(String(value)));
    },
  });
}

rewriteProperty(HTMLImageElement.prototype, "src", toWebViewUrl);
rewriteProperty(HTMLSourceElement.prototype, "src", toWebViewUrl);
rewriteProperty(HTMLAnchorElement.prototype, "href", toWebViewUrl);

for (const property of [
  "cssText",
  "background",
  "backgroundImage",
  "maskImage",
  "webkitMaskImage",
]) {
  rewriteProperty(CSSStyleDeclaration.prototype, property, toWebViewStyle);
}
const nativeSetProperty = CSSStyleDeclaration.prototype.setProperty;
CSSStyleDeclaration.prototype.setProperty = function (
  property,
  value,
  priority,
) {
  return nativeSetProperty.call(
    this,
    property,
    value ? toWebViewStyle(value) : value,
    priority,
  );
};
