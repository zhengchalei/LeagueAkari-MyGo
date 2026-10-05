import type { LatestReleaseInfo } from '@shared/types/akari'

export const TIMO_UPDATE_SOURCE_NOT_CONFIGURED = '尚未配置 Timo 更新源'
export const TIMO_UPDATE_ARCHIVE_INVALID = '更新包不属于当前 Timo 更新源'

export function getTimoUpdateRepository(value = process.env.TIMO_UPDATE_REPOSITORY) {
  const repository = value?.trim()
  if (
    !repository ||
    !/^[a-z\d](?:[a-z\d-]*[a-z\d])?\/[a-z\d_.-]+$/i.test(repository) ||
    repository.toLowerCase() === 'leagueakari/leagueakari'
  ) {
    return null
  }
  return repository
}

export function getTimoUpdateReleaseError(
  release: LatestReleaseInfo,
  repository = getTimoUpdateRepository()
) {
  if (!repository) {
    return TIMO_UPDATE_SOURCE_NOT_CONFIGURED
  }

  if (release.source !== 'github' || !/^Timo[-_.].*win.*\.7z$/i.test(release.archiveFile.name)) {
    return TIMO_UPDATE_ARCHIVE_INVALID
  }

  try {
    const downloadUrl = new URL(release.archiveFile.downloadUrl)
    if (
      downloadUrl.protocol === 'https:' &&
      downloadUrl.hostname === 'github.com' &&
      !downloadUrl.username &&
      !downloadUrl.password &&
      downloadUrl.pathname
        .toLowerCase()
        .startsWith(`/${repository.toLowerCase()}/releases/download/`)
    ) {
      return null
    }
  } catch {}

  return TIMO_UPDATE_ARCHIVE_INVALID
}
