import { describe, expect, it } from 'vitest'

import { parseCommandLine } from './ux-command-line-parser'

describe('League client command-line credentials', () => {
  it.each(['rso_platform_id', 'rso-platform-id'])(
    'reads quoted WeGame parameters with %s',
    (platformArgument) => {
      const auth = parseCommandLine(
        `"C:\\WeGameApps\\英雄联盟\\LeagueClient\\LeagueClientUx.exe" --app-port="57830" --app-pid="1234" --remoting-auth-token="local-test-token" --region="TENCENT" --${platformArgument}="HN1" --riotclient-app-port="5000" --riotclient-auth-token="riot-test-token"`
      )

      expect(auth).toMatchObject({
        port: 57830,
        pid: 1234,
        authToken: 'local-test-token',
        region: 'TENCENT',
        rsoPlatformId: 'HN1',
        riotClientPort: 5000,
        riotClientAuthToken: 'riot-test-token'
      })
    }
  )

  it('reads unquoted parameters without optional Riot client credentials', () => {
    expect(
      parseCommandLine('--app-port=57830 --app-pid=1234 --remoting-auth-token=local-test-token')
    ).toMatchObject({ port: 57830, pid: 1234, riotClientPort: 0, riotClientAuthToken: '' })
  })

  it.each([
    '--app-port=57830 --app-pid=1234',
    '--app-port=0 --app-pid=1234 --remoting-auth-token=test',
    '--app-port=70000 --app-pid=1234 --remoting-auth-token=test',
    '--app-port=57830 --app-pid=invalid --remoting-auth-token=test',
    '--other-app-port=57830 --app-pid=1234 --remoting-auth-token=test'
  ])('rejects missing or invalid client credentials', (commandLine) => {
    expect(parseCommandLine(commandLine)).toBeNull()
  })
})
