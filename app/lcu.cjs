const https = require('node:https');
const fs = require('node:fs/promises');
const path = require('node:path');
const { execFile } = require('node:child_process');
const { promisify } = require('node:util');
const runFile = promisify(execFile);

function commandValue(command, name) {
  const match = command?.match(new RegExp(`--${name}=(?:"([^"]+)"|([^\\s"]+))`));
  return match?.[1] || match?.[2];
}

function parseLockfile(text) {
  const [name, pid, port, password, protocol] = text.trim().split(':');
  if (!name || !Number(port) || !password || protocol !== 'https') return null;
  return { pid: Number(pid), port: Number(port), password };
}

async function discoverClient() {
  if (process.env.LOL_LOCKFILE) {
    try { return parseLockfile(await fs.readFile(process.env.LOL_LOCKFILE, 'utf8')); } catch {}
  }
  if (process.platform !== 'win32') return null;
  const script = await fs.readFile(path.join(__dirname, 'discover-client.ps1'), 'utf8');
  const { stdout } = await runFile('powershell.exe', ['-NoProfile', '-NonInteractive', '-Command', script], {
    windowsHide: true, timeout: 8000, maxBuffer: 256 * 1024
  });
  if (!stdout.trim()) return null;
  const processes = JSON.parse(stdout);
  for (const p of Array.isArray(processes) ? processes : [processes]) {
    const port = Number(commandValue(p.CommandLine, 'app-port'));
    const password = commandValue(p.CommandLine, 'remoting-auth-token');
    if (port && password) return { pid: p.ProcessId, port, password,
      region: commandValue(p.CommandLine, 'region'),
      platformId: commandValue(p.CommandLine, 'rso_platform_id') || commandValue(p.CommandLine, 'rso-platform-id') };
    if (p.ExecutablePath) {
      try {
        const auth = parseLockfile(await fs.readFile(path.join(path.dirname(p.ExecutablePath), 'lockfile'), 'utf8'));
        if (auth) return auth;
      } catch {}
    }
  }
  return null;
}

function requestLcu(auth, endpoint, method = 'GET', data) {
  if (!auth) return Promise.reject(new Error('客户端尚未连接'));
  const body = data === undefined ? null : JSON.stringify(data);
  return new Promise((resolve, reject) => {
    const request = https.request({
      hostname: '127.0.0.1', port: auth.port, path: endpoint, method,
      rejectUnauthorized: false,
      headers: {
        Authorization: `Basic ${Buffer.from(`riot:${auth.password}`).toString('base64')}`,
        ...(body ? { 'Content-Type': 'application/json', 'Content-Length': Buffer.byteLength(body) } : {})
      }
    }, response => {
      const chunks = [];
      response.on('data', chunk => chunks.push(chunk));
      response.on('error', reject);
      response.on('end', () => {
        const bytes = Buffer.concat(chunks);
        if (response.statusCode >= 400) {
          const error = new Error(`客户端接口返回 ${response.statusCode}`);
          error.status = response.statusCode;
          return reject(error);
        }
        resolve({ bytes, type: response.headers['content-type'] || 'application/octet-stream' });
      });
    });
    request.setTimeout(6000, () => request.destroy(new Error('客户端响应超时')));
    request.on('error', reject);
    if (body) request.write(body);
    request.end();
  });
}

async function lcuJson(auth, endpoint, method, data) {
  const response = await requestLcu(auth, endpoint, method, data);
  return response.bytes.length ? JSON.parse(response.bytes.toString('utf8')) : null;
}

module.exports = { discoverClient, requestLcu, lcuJson, parseLockfile, commandValue };
