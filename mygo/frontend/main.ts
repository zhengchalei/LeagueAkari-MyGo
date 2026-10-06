import "./runtime";
import "reflect-metadata";

import "@renderer-shared/assets/css/tailwind.css";
import "@renderer-shared/assets/css/base-styles.css";
import "@renderer-shared/assets/css/github-markdown.css";
import "@renderer-shared/assets/css/lol-view.css";
import "@renderer-shared/assets/css/theme-system.css";
import "@main-window/assets/css/styles.css";
import "@main-window/assets/css/transition.css";
import "./webview.css";

import { i18next } from "@renderer-shared/i18n";
import dayjs from "dayjs";
import "dayjs/locale/zh-cn";
import duration from "dayjs/plugin/duration";
import relativeTime from "dayjs/plugin/relativeTime";
import I18nextVue from "i18next-vue";
import { createPinia } from "pinia";
import { createApp } from "vue";

import NaiveUIProviderApp from "@main-window/NaiveUIProviderApp.vue";
import { router } from "@main-window/routes";
import { manager } from "@main-window/shards";
import { installSendSelection } from "./send-selection";

try {
  i18next.addResource(
    "zh-CN",
    "renderer",
    "settings.savedSettings.import.dialogWarning",
    "导入设置项将覆盖当前设置并立即应用。硬件加速设置将在下次启动时生效。是否继续？",
  );
  i18next.addResource(
    "en",
    "renderer",
    "settings.savedSettings.import.dialogWarning",
    "Importing overwrites your current settings and applies them immediately. Hardware acceleration changes take effect on the next launch. Continue?",
  );
  dayjs.extend(relativeTime);
  dayjs.extend(duration);

  const app = createApp(NaiveUIProviderApp)
    .use(router)
    .use(createPinia())
    .use(I18nextVue, { i18next })
    .use(manager);
  await manager.setup();
  installSendSelection();
  app.mount("#app");
} catch (error) {
  console.error("LeagueAkari-MyGo 无法正确加载：", error);
  const host = document.querySelector("#app");
  if (host) {
    host.textContent = `LeagueAkari-MyGo 无法加载：${error instanceof Error ? error.message : String(error)}`;
  }
}
