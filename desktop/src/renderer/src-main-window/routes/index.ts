import { createRouter, createWebHashHistory } from 'vue-router'

const Automation = () => import('@main-window/views/automation/Automation.vue')
const OngoingGame = () => import('@main-window/views/ongoing-game/OngoingGame.vue')
const PlayerTabs = () => import('@main-window/views/player-tabs/PlayerTabs.vue')
const Test = () => import('@main-window/views/test/Test.vue')
const Toolkit = () => import('@main-window/views/toolkit/Toolkit.vue')

// console.log(import.meta.env.BASE_URL)
const router = createRouter({
  history: createWebHashHistory(),
  routes: [
    {
      path: '/',
      name: 'root',
      redirect: { name: 'player-tabs' }
    },
    {
      name: 'player-tabs',
      path: '/player-tabs/:sgpServerId?/:puuid?',
      component: PlayerTabs
    },
    {
      name: 'ongoing-game',
      path: '/ongoing-game',
      component: OngoingGame
    },
    {
      name: 'toolkit',
      path: '/toolkit/:section?',
      component: Toolkit
    },
    {
      name: 'automation',
      path: '/automation/:section?',
      component: Automation
    },
    {
      name: 'test',
      path: '/test/:section?',
      component: Test
    }
  ]
})

export { router }
