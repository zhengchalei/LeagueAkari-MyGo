import { useInstance } from '@renderer-shared/shards'
import { AppCommonRenderer } from '@renderer-shared/shards/app-common'
import { defineComponent } from 'vue'

import { type SimpleNotificationsRendererContext } from './context'
import DeclarationModal from './modals/DeclarationModal.vue'
import { useSimpleNotificationsStore } from './store'

export function registerDeclarationModal(context: SimpleNotificationsRendererContext) {
  const Component = defineComponent({
    setup() {
      const appCommon = useInstance(AppCommonRenderer)
      const simpleNotificationsStore = useSimpleNotificationsStore()

      return () => (
        <DeclarationModal
          {...{
            show: simpleNotificationsStore.showDeclarationModal,
            'onUpdate:show': (value: boolean) =>
              (simpleNotificationsStore.showDeclarationModal = value),
            onConfirm: () => {
              appCommon.setShowFreeSoftwareDeclaration(false)
              simpleNotificationsStore.showDeclarationModal = false
            },
            onExit: () => {
              appCommon.exit()
            }
          }}
        />
      )
    }
  })

  context.setupInAppScope.addRenderVNode(() => <Component />)
}
