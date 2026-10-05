import { afterEach, describe, expect, it, vi } from 'vitest'

import type { RemoteConfigMainContext } from './context'
import { RemoteConfigReleaseController } from './release-controller'

vi.mock('electron', () => ({ app: { getVersion: () => '1.0.0' } }))
vi.mock('@main/utils/timer', () => ({
  IntervalTask: class {
    start() {}
    cancel() {}
  }
}))

function createContext() {
  const repository = {
    getTimoLatestRelease: vi.fn(),
    getLatestRelease: vi.fn(),
    getRawContent: vi.fn()
  }
  const state = {
    isUpdatingLatestRelease: false,
    latestRelease: null,
    setLatestRelease: vi.fn(),
    setUpdatingLatestRelease: vi.fn()
  }
  const akariApi = { getLastResortLatestRelease: vi.fn() }
  const context = {
    repository,
    state,
    akariApi,
    logger: { info: vi.fn(), warn: vi.fn() },
    settings: { updateLatestRelease: true },
    mobxUtils: {
      reaction: (read: () => boolean, update: (value: boolean) => void) => update(read())
    }
  } as unknown as RemoteConfigMainContext
  return { context, repository, state, akariApi }
}

describe('Timo release isolation', () => {
  afterEach(() => vi.unstubAllEnvs())

  it('does not check Akari or any remote release when Timo has no update repository', async () => {
    vi.stubEnv('TIMO_UPDATE_REPOSITORY', '')
    const { context, repository, state, akariApi } = createContext()
    const controller = new RemoteConfigReleaseController(context)
    controller.watch()

    await expect(controller.updateLatestReleaseManually()).rejects.toThrow('尚未配置 Timo 更新源')
    await expect(controller.getLatestReleaseFromLastResort()).rejects.toThrow(
      '尚未配置 Timo 更新源'
    )
    expect(repository.getTimoLatestRelease).not.toHaveBeenCalled()
    expect(repository.getLatestRelease).not.toHaveBeenCalled()
    expect(akariApi.getLastResortLatestRelease).not.toHaveBeenCalled()
    expect(state.setLatestRelease).toHaveBeenCalledWith(null)
  })

  it('loads only the configured GitHub release and uses its Timo archive', async () => {
    vi.stubEnv('TIMO_UPDATE_REPOSITORY', 'owner/timo')
    const { context, repository, state } = createContext()
    repository.getTimoLatestRelease.mockResolvedValue({
      data: {
        tag_name: 'v2.0.0',
        body: 'Timo release notes',
        published_at: '2026-10-05T00:00:00Z',
        assets: [
          {
            name: 'Timo-2.0.0-win.7z',
            size: 100,
            content_type: 'application/x-7z-compressed',
            browser_download_url:
              'https://github.com/owner/timo/releases/download/v2.0.0/Timo-2.0.0-win.7z'
          }
        ]
      }
    })

    const release = await new RemoteConfigReleaseController(context).updateLatestReleaseManually()
    expect(release).toMatchObject({ version: 'v2.0.0', source: 'github', isNew: true })
    expect(repository.getTimoLatestRelease).toHaveBeenCalledWith('owner/timo')
    expect(repository.getRawContent).not.toHaveBeenCalled()
    expect(repository.getLatestRelease).not.toHaveBeenCalled()
    expect(state.setLatestRelease).toHaveBeenCalledWith(release)
  })
})
