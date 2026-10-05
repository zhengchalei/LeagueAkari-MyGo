import { IntervalTask } from '@main/utils/timer'
import { LatestReleaseInfo } from '@shared/types/akari'
import { app } from 'electron'
import { gt } from 'semver'

import {
  TIMO_UPDATE_SOURCE_NOT_CONFIGURED,
  getTimoUpdateReleaseError,
  getTimoUpdateRepository
} from '../self-update/update-source'
import {
  REMOTE_CONFIG_VOLATILE_RESOURCE_UPDATE_INTERVAL,
  type RemoteConfigMainContext
} from './context'
import { hasReachedRemoteRateLimit } from './rate-limit'

export class RemoteConfigReleaseController {
  private readonly _latestReleaseTask = new IntervalTask(this._updateFromRemote.bind(this), {
    interval: REMOTE_CONFIG_VOLATILE_RESOURCE_UPDATE_INTERVAL
  })

  constructor(private readonly _context: RemoteConfigMainContext) {}

  watch() {
    const { mobxUtils, settings, state } = this._context

    mobxUtils.reaction(
      () => settings.updateLatestRelease,
      (updateLatestRelease) => {
        if (updateLatestRelease && getTimoUpdateRepository()) {
          this._latestReleaseTask.start({ runImmediately: true })
        } else {
          this._latestReleaseTask.cancel()
          state.setLatestRelease(null)
        }
      },
      { fireImmediately: true }
    )
  }

  async updateLatestReleaseManually() {
    const { logger, state } = this._context
    if (!getTimoUpdateRepository()) {
      state.setLatestRelease(null)
      throw new Error(TIMO_UPDATE_SOURCE_NOT_CONFIGURED)
    }

    if (state.isUpdatingLatestRelease) {
      return state.latestRelease
    }

    state.setUpdatingLatestRelease(true)
    this._latestReleaseTask.cancel()

    try {
      const release = await this._fetchLatestRelease()
      state.setLatestRelease(release)
      this._latestReleaseTask.start()
      logger.info('Updated Timo release from configured GitHub repository')
      return release
    } finally {
      state.setUpdatingLatestRelease(false)
    }
  }

  // Keep the public contract, but never fall back to Akari's release service.
  async getLatestReleaseFromLastResort(): Promise<LatestReleaseInfo> {
    return this._fetchLatestRelease()
  }

  private async _fetchLatestRelease(): Promise<LatestReleaseInfo> {
    const repository = getTimoUpdateRepository()
    if (!repository) {
      throw new Error(TIMO_UPDATE_SOURCE_NOT_CONFIGURED)
    }

    const { data } = await this._context.repository.getTimoLatestRelease(repository)
    const currentVersion = app.getVersion()
    const asset = data.assets.find((asset) => /^Timo[-_.].*win.*\.7z$/i.test(asset.name))
    if (!asset) {
      throw new Error('发布中没有 Timo Windows 更新包')
    }

    const release: LatestReleaseInfo = {
      version: data.tag_name,
      currentVersion,
      isNew: gt(data.tag_name, currentVersion),
      source: 'github',
      publishedAt: data.published_at || data.created_at,
      description: data.body,
      archiveFile: {
        name: asset.name,
        size: asset.size,
        downloadUrl: asset.browser_download_url,
        contentType: asset.content_type
      }
    }
    const validationError = getTimoUpdateReleaseError(release, repository)
    if (validationError) {
      throw new Error(validationError)
    }
    return release
  }

  private async _updateFromRemote() {
    const { logger, state } = this._context
    if (!getTimoUpdateRepository()) {
      state.setLatestRelease(null)
      return
    }
    if (state.isUpdatingLatestRelease) {
      return
    }

    state.setUpdatingLatestRelease(true)
    try {
      state.setLatestRelease(await this._fetchLatestRelease())
      logger.info('Updated Timo release from configured GitHub repository')
    } catch (error) {
      if (!hasReachedRemoteRateLimit(error, logger)) {
        logger.warn('Failed to check configured Timo update source', error)
      }
    } finally {
      state.setUpdatingLatestRelease(false)
    }
  }
}
