import { describe, expect, it, vi } from 'vitest'

import type { SgpMainContext } from './context'
import { SgpTokenStateController } from './token-state-controller'

describe('SGP token readiness', () => {
  it('updates readiness without including credential fragments in logs', () => {
    const entitlementsToken = 'sensitive-entitlements-test-token'
    const leagueSessionToken = 'sensitive-league-session-test-token'
    const logger = { info: vi.fn() }
    const state = {
      setEntitlementsTokenSet: vi.fn(),
      setLeagueSessionTokenSet: vi.fn()
    }
    const context = {
      leagueClient: {
        data: {
          entitlements: { token: { accessToken: entitlementsToken, token: entitlementsToken } },
          leagueSession: { token: leagueSessionToken }
        }
      },
      logger,
      state,
      mobxUtils: {
        reaction: (read: () => unknown, update: (value: unknown) => void) => update(read())
      }
    } as unknown as SgpMainContext

    new SgpTokenStateController(context).watch()

    expect(state.setEntitlementsTokenSet).toHaveBeenLastCalledWith(true)
    expect(state.setLeagueSessionTokenSet).toHaveBeenLastCalledWith(true)
    const loggedMessages = JSON.stringify(logger.info.mock.calls)
    expect(loggedMessages).not.toContain(entitlementsToken.slice(0, 24))
    expect(loggedMessages).not.toContain(leagueSessionToken.slice(0, 24))
  })
})
