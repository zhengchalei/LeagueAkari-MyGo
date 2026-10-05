const { app, BrowserWindow } = require('electron');
const path = require('node:path');
const { startServer } = require('./server.cjs');

let localServer;
if (!app.requestSingleInstanceLock()) app.quit();
else {
  app.on('second-instance', (_, argv) => {
    const window = BrowserWindow.getAllWindows()[0];
    if (window && localServer) {
      if (window.isMinimized()) window.restore();
      void window.loadURL(`${localServer.origin}/${argv.includes('--demo') ? '?demo=1' : ''}`);
      window.focus();
    }
  });
  app.whenReady().then(async () => {
    localServer = await startServer({ cacheDir: path.join(app.getPath('userData'), 'cache') });
    const window = new BrowserWindow({
      title: 'Timo · 选人助手', width: 760, height: 900, minWidth: 420, minHeight: 540,
      autoHideMenuBar: true, backgroundColor: '#f7f7f8',
      webPreferences: { nodeIntegration: false, contextIsolation: true, sandbox: true }
    });
    window.webContents.setWindowOpenHandler(() => ({ action: 'deny' }));
    window.webContents.on('will-navigate', (event, url) => { if (new URL(url).origin !== localServer.origin) event.preventDefault(); });
    await window.loadURL(`${localServer.origin}/${process.argv.includes('--demo') ? '?demo=1' : ''}`);
  }).catch(error => { console.error(error.message); app.quit(); });
  app.on('window-all-closed', () => app.quit());
  app.on('before-quit', () => { localServer?.assistant.close(); localServer?.server.close(); });
}
