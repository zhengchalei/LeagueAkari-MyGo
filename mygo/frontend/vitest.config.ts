import { defineConfig } from "../../desktop/node_modules/vitest/dist/config.js";
import { resolve } from "node:path";
import { fileURLToPath } from "node:url";

const frontend = fileURLToPath(new URL(".", import.meta.url));
const desktop = resolve(frontend, "../../desktop");

export default defineConfig({
  root: frontend,
  resolve: {
    alias: {
      "@shared": resolve(desktop, "src/shared"),
      "@renderer-shared": resolve(desktop, "src/renderer-shared"),
      vue: resolve(desktop, "node_modules/vue/dist/vue.runtime.esm-bundler.js"),
      lodash: resolve(desktop, "node_modules/lodash/lodash.js"),
      vitest: resolve(desktop, "node_modules/vitest/dist/index.js"),
    },
  },
  test: { include: ["ongoing-analysis.test.ts"], environment: "node" },
});
