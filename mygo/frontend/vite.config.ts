import yaml from "../../desktop/node_modules/@modyfi/vite-plugin-yaml/dist/index.js";
import tailwindcss from "../../desktop/node_modules/@tailwindcss/vite/dist/index.mjs";
import vue from "../../desktop/node_modules/@vitejs/plugin-vue/dist/index.mjs";
import vueJsx from "../../desktop/node_modules/@vitejs/plugin-vue-jsx/dist/index.mjs";
import { defineConfig } from "../../desktop/node_modules/vite/dist/node/index.js";
import { dirname, resolve } from "node:path";
import { fileURLToPath } from "node:url";

const frontend = dirname(fileURLToPath(import.meta.url));
const desktop = resolve(frontend, "../../desktop");
const frontendModulePrefix = frontend.replaceAll("\\", "/");
const LC_CUSTOM_TAGS = new Set([
  "mainText",
  "stats",
  "active",
  "passive",
  "attention",
  "rarityMythic",
  "rarityLegendary",
  "rarityGeneric",
  "keywordStealth",
  "scaleArmor",
  "scaleMR",
  "scaleAD",
  "scaleAP",
  "feSteal",
  "flavorText",
  "rules",
  "status",
  "speed",
  "shield",
  "heang",
  "scaleMana",
  "scalemana",
  "magicDamage",
  "trueDamage",
  "physicalDamage",
  "ornnBonus",
  "buffedStat",
  "nerfedStat",
  "keywordMajor",
]);

export default defineConfig({
  root: frontend,
  base: "./",
  resolve: {
    alias: {
      "@main-window": resolve(desktop, "src/renderer/src-main-window"),
      "@aux-window": resolve(desktop, "src/renderer/src-aux-window"),
      "@opgg-window": resolve(desktop, "src/renderer/src-opgg-window"),
      "@ongoing-game-window": resolve(
        desktop,
        "src/renderer/src-ongoing-game-window",
      ),
      "@cd-timer-window": resolve(desktop, "src/renderer/src-cd-timer-window"),
      "@shared": resolve(desktop, "src/shared"),
      "@renderer-shared": resolve(desktop, "src/renderer-shared"),
    },
  },
  plugins: [
    {
      name: "desktop-dependencies",
      enforce: "pre",
      async resolveId(source, importer) {
        if (
          !importer?.replaceAll("\\", "/").startsWith(frontendModulePrefix) ||
          source.startsWith(".") ||
          source.startsWith("/") ||
          source.startsWith("@main-window") ||
          source.startsWith("@renderer-shared") ||
          source.startsWith("@shared")
        ) {
          return null;
        }
        return this.resolve(
          source,
          resolve(desktop, "src/renderer/src-main-window/main.ts"),
          {
            skipSelf: true,
          },
        );
      },
      transform(code, id) {
        if (
          id
            .replaceAll("\\", "/")
            .endsWith("/ongoing-game/store-event-handlers.ts")
        ) {
          return {
            code: code.replace(
              "store.cachedGames[entry.data.gameId] = markRaw(entry.data)",
              "store.cachedGames[entry.gameId] = markRaw(entry)",
            ),
            map: null,
          };
        }
        if (
          id
            .replaceAll("\\", "/")
            .endsWith("/renderer-shared/shards/ongoing-game/index.ts")
        ) {
          const analysisModule = resolve(
            frontend,
            "ongoing-analysis.ts",
          ).replaceAll("\\", "/");
          return {
            code:
              `import { installOngoingGameAnalysis } from ${JSON.stringify(analysisModule)}\n` +
              code
                .replace(
                  "private readonly _context: OngoingGameRendererContext",
                  "private _disposeMyGoAnalysis?: () => void\n  private readonly _context: OngoingGameRendererContext",
                )
                .replace(
                  "await this._storeEventHandlers.loadInitialData(await this.getAll())",
                  "await this._storeEventHandlers.loadInitialData(await this.getAll())\n    this._disposeMyGoAnalysis = installOngoingGameAnalysis(store)",
                )
                .replace(
                  "async onDispose() {}",
                  "async onDispose() { this._disposeMyGoAnalysis?.() }",
                ),
            map: null,
          };
        }
        if (
          id.replaceAll("\\", "/").endsWith("/player-tab/data/match-history.ts")
        ) {
          return {
            code: code.replace(
              /^[ \t]*loadGameDetails\((?:games|gamesToAppend|completedGamesToAppend)\)[ \t]*$/gm,
              "",
            ),
            map: null,
          };
        }
        if (
          id.replaceAll("\\", "/").endsWith("/providers/game-resource/akari.ts")
        ) {
          return {
            code: code.replace("https?:|image:", "https?:|akari:|image:"),
            map: null,
          };
        }
        if (
          /\.(vue|css)(\?|$)/.test(id) &&
          code.includes("-webkit-app-region")
        ) {
          return {
            code: code.replaceAll("-webkit-app-region", "--app-region"),
            map: null,
          };
        }
        return null;
      },
      generateBundle(_options, bundle) {
        for (const output of Object.values(bundle)) {
          if (
            output.type === "asset" &&
            output.fileName.endsWith(".css") &&
            typeof output.source === "string"
          ) {
            output.source = output.source.replaceAll(
              "-webkit-app-region",
              "--app-region",
            );
          }
        }
      },
    },
    yaml(),
    vue({
      template: {
        compilerOptions: { isCustomElement: (tag) => LC_CUSTOM_TAGS.has(tag) },
      },
    }),
    tailwindcss(),
    vueJsx({
      tsTransform: "built-in",
      babelPlugins: [
        [
          resolve(
            desktop,
            "node_modules/@babel/plugin-syntax-decorators/lib/index.js",
          ),
          { legacy: true },
        ],
      ],
    }),
  ],
  build: {
    target: "es2022",
    outDir: resolve(frontend, "dist"),
    emptyOutDir: true,
    rolldownOptions: {
      input: {
        mainWindow: resolve(frontend, "main-window.html"),
        auxWindow: resolve(frontend, "aux-window.html"),
        opggWindow: resolve(frontend, "opgg-window.html"),
        ongoingGameWindow: resolve(frontend, "ongoing-game-window.html"),
        cdTimerWindow: resolve(frontend, "cd-timer-window.html"),
      },
    },
  },
});
