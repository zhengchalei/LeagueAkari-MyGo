import { type Environment, builtinEnvironments } from 'vitest/runtime'

// Compile Vue templates for the client while the test hosts them with a custom renderer.
export default {
  ...builtinEnvironments.node,
  name: 'vue-client-renderer',
  viteEnvironment: 'client'
} satisfies Environment
