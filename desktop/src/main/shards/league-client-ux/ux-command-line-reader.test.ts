import { beforeEach, describe, expect, it, vi } from 'vitest'

import type { LeagueClientUxMainContext } from './context'
import { LeagueClientUxCommandLineReader } from './ux-command-line-reader'

const native = vi.hoisted(() => ({
  getCommandLine: vi.fn(),
  getPidsByName: vi.fn()
}))

vi.mock('@main/native', () => ({ ...native, isElevated: false }))

function createReader(useWmi = false) {
  const state = { setHasClientButNoCommandLine: vi.fn() }
  const context = { settings: { useWmi }, state } as unknown as LeagueClientUxMainContext
  return { reader: new LeagueClientUxCommandLineReader(context), state }
}

describe('League client discovery without elevation', () => {
  beforeEach(() => {
    vi.resetAllMocks()
    native.getPidsByName.mockResolvedValue([1234])
    native.getCommandLine.mockResolvedValue(
      '--app-port=57830 --app-pid=1234 --remoting-auth-token=test --region=TENCENT --rso_platform_id=HN1'
    )
  })

  it('keeps native discovery available when WMI was enabled previously', async () => {
    const { reader } = createReader(true)

    expect(await reader.read()).toMatchObject([{ port: 57830, pid: 1234, region: 'TENCENT' }])
    expect(native.getCommandLine).toHaveBeenCalledWith(1234, { win32QueryType: 'native' })
  })

  it('clears the access warning when valid client credentials become available', async () => {
    const { reader, state } = createReader()
    native.getCommandLine.mockResolvedValue('Failed to retrieve command line.')
    for (let attempt = 0; attempt < 5; attempt++) {
      expect(await reader.read()).toEqual([])
    }
    expect(state.setHasClientButNoCommandLine).toHaveBeenLastCalledWith(true)

    native.getCommandLine.mockResolvedValue(
      '--app-port=57830 --app-pid=1234 --remoting-auth-token=test'
    )
    expect(await reader.read()).toHaveLength(1)
    expect(state.setHasClientButNoCommandLine).toHaveBeenLastCalledWith(false)

    native.getCommandLine.mockResolvedValue('Failed to retrieve command line.')
    await reader.read()
    expect(state.setHasClientButNoCommandLine).toHaveBeenLastCalledWith(false)
  })
})
