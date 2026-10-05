import { is } from '@electron-toolkit/utils'

export const DEEP_LINK_PROTOCOL = is.dev ? 'timo-dev' : 'timo'

export const DEEP_LINK_PROTOCOL_PROD = 'timo'

export const DEEP_LINK_PROTOCOL_DEV = 'timo-dev'
