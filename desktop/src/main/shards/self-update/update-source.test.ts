import type { LatestReleaseInfo } from '@shared/types/akari'
import { describe, expect, it } from 'vitest'

import {
  TIMO_UPDATE_ARCHIVE_INVALID,
  getTimoUpdateReleaseError,
  getTimoUpdateRepository
} from './update-source'

describe('Timo update source', () => {
  it.each(['', 'https://github.com/owner/repo', '../repo', 'LeagueAkari/LeagueAkari'])(
    'rejects an absent or invalid repository',
    (value) => expect(getTimoUpdateRepository(value)).toBeNull()
  )

  it('accepts the explicitly configured GitHub repository', () => {
    expect(getTimoUpdateRepository(' owner/Timo-releases ')).toBe('owner/Timo-releases')
  })

  it.each([
    ['LeagueAkari-2.0.0-win.7z', 'https://github.com/owner/timo/releases/download/v2.0.0/akari.7z'],
    [
      'Timo-2.0.0-win.7z',
      'https://github.com/LeagueAkari/LeagueAkari/releases/download/v2.0.0/Timo.7z'
    ],
    ['Timo-2.0.0-win.7z', 'https://example.com/owner/timo/releases/download/v2.0.0/Timo.7z']
  ])('rejects upstream or foreign update archives', (name, downloadUrl) => {
    const release = {
      source: 'github',
      archiveFile: { name, downloadUrl }
    } as LatestReleaseInfo
    expect(getTimoUpdateReleaseError(release, 'owner/timo')).toBe(TIMO_UPDATE_ARCHIVE_INVALID)
  })

  it('accepts a Timo archive from the configured repository', () => {
    const release = {
      source: 'github',
      archiveFile: {
        name: 'Timo-2.0.0-win.7z',
        downloadUrl: 'https://github.com/owner/timo/releases/download/v2.0.0/Timo-2.0.0-win.7z'
      }
    } as LatestReleaseInfo
    expect(getTimoUpdateReleaseError(release, 'owner/timo')).toBeNull()
  })
})
