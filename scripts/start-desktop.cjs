const fs = require("node:fs");
const path = require("node:path");
const { spawn } = require("node:child_process");

const executable = path.resolve(
  __dirname,
  "../mygo/build/LeagueAkari-MyGo.exe",
);
if (!fs.existsSync(executable)) {
  console.error("请先执行 npm run desktop:install 和 npm run mygo:build");
  process.exitCode = 1;
} else {
  // 桌面助手需要显示主窗口；隐藏启动会让后续打开被后台实例拦住。
  const child = spawn(executable, [], { stdio: "inherit", windowsHide: false });
  child.on("error", (error) => {
    console.error(error.message);
    process.exitCode = 1;
  });
  child.on("exit", (code) => {
    process.exitCode = code || 0;
  });
}
