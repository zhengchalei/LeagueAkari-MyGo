import { afterEach, describe, expect, it, vi } from 'vitest'

import type { SelfUpdateMainContext } from './context'
import { SelfUpdateIpcHandlers } from './ipc-handlers'
import type { SelfUpdateUninstaller } from './uninstaller'
import type { SelfUpdateExecutor } from './update-executor'

vi.mock('electron', () => ({ app: {}, shell: {} }))

describe('Timo update IPC', () => {
  afterEach(() => vi.unstubAllEnvs())

  it('returns an explicit unconfigured source result for check, start and force-start', async () => {
    vi.stubEnv('TIMO_UPDATE_REPOSITORY', '')
    const handlers = new Map<string, () => unknown>()
    const executor = { start: vi.fn() }
    const remoteConfig = { updateLatestReleaseManually: vi.fn(), state: { latestRelease: {} } }
    const context = {
      namespace: 'self-update-main',
      ipc: {
        onCall: (_namespace: string, name: string, handler: () => unknown) =>
          handlers.set(name, handler)
      },
      remoteConfig
    } as unknown as SelfUpdateMainContext

    new SelfUpdateIpcHandlers(
      context,
      executor as unknown as SelfUpdateExecutor,
      {} as SelfUpdateUninstaller
    ).register()

    for (const name of ['checkUpdates', 'startUpdate', 'forceStartUpdate']) {
      await expect(handlers.get(name)!()).resolves.toEqual({
        result: 'failed',
        reason: '尚未配置 Timo 更新源'
      })
    }
    expect(executor.start).not.toHaveBeenCalled()
    expect(remoteConfig.updateLatestReleaseManually).not.toHaveBeenCalled()
  })
})
