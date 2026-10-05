const fs = require("node:fs");
const path = require("node:path");
const { spawnSync } = require("node:child_process");

const root = path.resolve(__dirname, "..");
const project = path.join(root, "mygo");
function run(command, args, cwd) {
  const result = spawnSync(command, args, {
    cwd,
    stdio: "inherit",
    windowsHide: true,
  });
  if (result.error) throw result.error;
  if (result.status !== 0) process.exit(result.status || 1);
}

run(
  process.execPath,
  [
    "../desktop/node_modules/vite/bin/vite.js",
    "build",
    "--config",
    "frontend/vite.config.ts",
  ],
  project,
);
fs.mkdirSync(path.join(project, "build"), { recursive: true });
run(
  "go",
  [
    "build",
    "-trimpath",
    "-ldflags",
    "-s -w -H=windowsgui -X github.com/egoist/mygo.production=1",
    "-o",
    "build/LeagueAkari-MyGo.exe",
    ".",
  ],
  project,
);
fs.copyFileSync(
  path.join(root, "LICENSE"),
  path.join(project, "build/LICENSE.txt"),
);
const releaseReadme = fs
  .readFileSync(path.join(root, "README.md"), "utf8")
  .replace(
    /\]\((?!https?:\/\/)([^)]+)\)/g,
    "](" + "https://github.com/zhengchalei/LeagueAkari-MyGo/blob/main/$1)",
  );
fs.writeFileSync(path.join(project, "build/README.md"), releaseReadme);
fs.copyFileSync(
  path.join(root, "desktop/LICENSE"),
  path.join(project, "build/LeagueAkari-LICENSE.txt"),
);
fs.copyFileSync(
  path.join(root, "THIRD_PARTY_NOTICES.md"),
  path.join(project, "build/THIRD_PARTY_NOTICES.md"),
);
const moduleInfo = spawnSync(
  "go",
  ["list", "-m", "-f", "{{.Dir}}", "github.com/egoist/mygo"],
  { cwd: project, encoding: "utf8", windowsHide: true },
);
if (moduleInfo.status !== 0) throw new Error(moduleInfo.stderr);
const mygoLicense = path.join(project, "build/MyGo-LICENSE.txt");
if (fs.existsSync(mygoLicense)) fs.chmodSync(mygoLicense, 0o666);
fs.writeFileSync(
  mygoLicense,
  fs.readFileSync(path.join(moduleInfo.stdout.trim(), "LICENSE")),
);
console.log("Built: mygo/build/LeagueAkari-MyGo.exe");
