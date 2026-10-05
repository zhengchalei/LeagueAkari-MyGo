const http = require('node:http');
const fs = require('node:fs/promises');
const path = require('node:path');
const crypto = require('node:crypto');
const { Assistant } = require('./assistant.cjs');

const MIME = { '.html': 'text/html; charset=utf-8', '.css': 'text/css; charset=utf-8', '.js': 'application/javascript; charset=utf-8', '.json': 'application/json; charset=utf-8', '.png': 'image/png', '.jpg': 'image/jpeg', '.webp': 'image/webp' };

async function fileInside(root, relative) {
  const absolute = path.resolve(root, relative);
  const resolvedRoot = await fs.realpath(root);
  const resolvedFile = await fs.realpath(absolute);
  const relation = path.relative(resolvedRoot, resolvedFile);
  if (relation === '..' || relation.startsWith(`..${path.sep}`) || path.isAbsolute(relation)) throw new Error('文件路径不可用');
  return resolvedFile;
}

async function readBody(request) {
  const chunks = [];
  let length = 0;
  for await (const chunk of request) {
    length += chunk.length;
    if (length > 4096) throw new Error('请求过大');
    chunks.push(chunk);
  }
  return JSON.parse(Buffer.concat(chunks).toString('utf8') || '{}');
}

async function startServer(options = {}) {
  const assistant = options.assistant || new Assistant(options);
  if (!options.assistant) await assistant.init();
  const token = crypto.randomBytes(24).toString('hex');
  let origin;
  const imageCache = new Map();
  const server = http.createServer(async (request, response) => {
    response.setHeader('X-Content-Type-Options', 'nosniff');
    response.setHeader('Cache-Control', 'no-store');
    function json(status, data) {
      response.writeHead(status, { 'Content-Type': 'application/json; charset=utf-8' });
      response.end(JSON.stringify(data));
    }
    try {
      if (request.headers.host !== new URL(origin).host) return json(403, { error: '无效的本地访问地址' });
      const url = new URL(request.url, origin);
      if (request.method === 'GET' && url.pathname === '/api/state') return json(200, { ...assistant.getState(url.searchParams.get('demo') === '1'), actionToken: token });
      if (request.method === 'POST' && ['/api/hero', '/api/skin'].includes(url.pathname)) {
        if (request.headers['x-assistant-token'] !== token || (request.headers.origin && request.headers.origin !== origin)) return json(403, { error: '请从助手界面操作' });
        const body = await readBody(request);
        if (!Number.isInteger(body.id) || body.id < 1) return json(400, { error: '无效的选择' });
        const state = url.pathname === '/api/hero' ? await assistant.selectHero(body.id, body.demo === true) : await assistant.selectSkin(body.id, body.demo === true);
        return json(200, { ...state, actionToken: token });
      }
      if (request.method !== 'GET') return json(405, { error: '不支持的操作' });
      if (url.pathname === '/api/image') {
        const assetPath = url.searchParams.get('path') || '';
        if (!assetPath.startsWith('/lol-game-data/assets/') || assetPath.includes('..')) return json(400, { error: '无效的资源路径' });
        const clientId = assistant.auth?.pid;
        const cacheKey = `${clientId}:${assetPath}`;
        let image = imageCache.get(cacheKey);
        if (!image) {
          image = await assistant.binary(assistant.auth, assetPath);
          if (!image.type.startsWith('image/')) return json(400, { error: '资源不是图片' });
          if (imageCache.size >= 100) imageCache.delete(imageCache.keys().next().value);
          imageCache.set(cacheKey, image);
        }
        response.writeHead(200, { 'Content-Type': image.type, 'Cache-Control': 'private, max-age=300' });
        return response.end(image.bytes);
      }
      const root = url.pathname.startsWith('/assets/') ? assistant.assetsDir : path.join(__dirname, 'public');
      const relative = url.pathname.startsWith('/assets/') ? decodeURIComponent(url.pathname.slice(8)) : (url.pathname === '/' ? 'index.html' : decodeURIComponent(url.pathname.slice(1)));
      const extension = path.extname(relative).toLowerCase();
      if (!MIME[extension]) return json(404, { error: '文件不存在' });
      const file = await fileInside(root, relative);
      const bytes = await fs.readFile(file);
      response.setHeader('Content-Security-Policy', "default-src 'self'; script-src 'self'; style-src 'self'; img-src 'self' data:; connect-src 'self'; object-src 'none'; frame-ancestors 'none'");
      response.writeHead(200, { 'Content-Type': MIME[extension] });
      response.end(bytes);
    } catch (error) {
      json(error.code === 'ENOENT' ? 404 : 400, { error: error.message || '操作失败，请重试' });
    }
  });
  await new Promise((resolve, reject) => { server.once('error', reject); server.listen(options.port ?? 0, '127.0.0.1', resolve); });
  origin = `http://127.0.0.1:${server.address().port}`;
  return { server, assistant, origin, close: () => { assistant.close(); return new Promise(resolve => server.close(resolve)); } };
}

if (require.main === module) startServer({ port: Number(process.env.PORT || 8792) }).then(app => {
  console.log(`选人助手：${app.origin}`);
  for (const signal of ['SIGINT', 'SIGTERM']) process.on(signal, () => void app.close().then(() => process.exit()));
}).catch(error => { console.error(error.message); process.exitCode = 1; });

module.exports = { startServer, fileInside };
