const fs = require("node:fs");
const path = require("node:path");
const { spawnSync } = require("node:child_process");

const root = path.resolve(__dirname, "..");
const project = path.join(root, "mygo");
const output = path.resolve(project, process.argv[2] || "build");
const version = JSON.parse(
  fs.readFileSync(path.join(root, "package.json"), "utf8"),
).version;
function run(command, args, cwd) {
  const result = spawnSync(command, args, {
    cwd,
    stdio: "inherit",
    windowsHide: true,
  });
  if (result.error) throw result.error;
  if (result.status !== 0)
    throw new Error(`${command} failed with exit code ${result.status}`);
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
fs.mkdirSync(output, { recursive: true });
const resourceConfig = path.join(output, "winres.json");
fs.writeFileSync(
  resourceConfig,
  JSON.stringify(
    {
      RT_GROUP_ICON: {
        "#1": {
          "0000": path.relative(output, path.join(project, "assets/icon.png")),
        },
      },
      RT_VERSION: {
        "#1": {
          "0000": {
            fixed: { file_version: version, product_version: version },
            info: {
              "0409": {
                FileDescription: "LeagueAkari-MyGo · 轻量级 LOL 助手",
                FileVersion: version,
                ProductName: "LeagueAkari-MyGo",
                ProductVersion: version,
                OriginalFilename: "LeagueAkari-MyGo.exe",
              },
            },
          },
        },
      },
    },
    null,
    2,
  ),
);
try {
  // Window icons do not reach Explorer; embed the app icon in the executable.
  run(
    "go",
    [
      "run",
      "github.com/tc-hib/go-winres@v0.3.3",
      "make",
      "--in",
      resourceConfig,
      "--arch",
      "amd64",
      "--out",
      "rsrc",
    ],
    project,
  );
  run(
    "go",
    [
      "build",
      "-trimpath",
      "-ldflags",
      "-s -w -H=windowsgui -X github.com/egoist/mygo.production=1",
      "-o",
      path.join(output, "LeagueAkari-MyGo.exe"),
      ".",
    ],
    project,
  );
} finally {
  fs.rmSync(path.join(project, "rsrc_windows_amd64.syso"), { force: true });
  fs.rmSync(resourceConfig, { force: true });
}
fs.copyFileSync(path.join(root, "LICENSE"), path.join(output, "LICENSE.txt"));
const releaseReadme = fs
  .readFileSync(path.join(root, "README.md"), "utf8")
  .replace(
    /\]\((?!https?:\/\/)([^)]+)\)/g,
    "](" + "https://github.com/zhengchalei/LeagueAkari-MyGo/blob/main/$1)",
  );
fs.writeFileSync(path.join(output, "README.md"), releaseReadme);
fs.copyFileSync(
  path.join(root, "desktop/LICENSE"),
  path.join(output, "LeagueAkari-LICENSE.txt"),
);
fs.copyFileSync(
  path.join(root, "THIRD_PARTY_NOTICES.md"),
  path.join(output, "THIRD_PARTY_NOTICES.md"),
);
const moduleInfo = spawnSync(
  "go",
  ["list", "-m", "-f", "{{.Dir}}", "github.com/egoist/mygo"],
  { cwd: project, encoding: "utf8", windowsHide: true },
);
if (moduleInfo.status !== 0) throw new Error(moduleInfo.stderr);
const mygoLicense = path.join(output, "MyGo-LICENSE.txt");
if (fs.existsSync(mygoLicense)) fs.chmodSync(mygoLicense, 0o666);
fs.writeFileSync(
  mygoLicense,
  fs.readFileSync(path.join(moduleInfo.stdout.trim(), "LICENSE")),
);
console.log(`Built: ${path.join(output, "LeagueAkari-MyGo.exe")}`);
