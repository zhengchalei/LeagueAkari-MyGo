import { BalanceType } from '@shared/data-sources/fandom'
import { GtimgHeroListJs, GtimgKiwiAugments } from '@shared/data-sources/gtimg'
import type { OpggAramBalanceItem } from '@shared/types/opgg'
import { makeAutoObservable, observable } from 'mobx'

export class ExtraAssetsStateGtimg {
  heroList: GtimgHeroListJs | null
  kiwiAugments: GtimgKiwiAugments[] | null

  setHeroList(heroList: GtimgHeroListJs | null) {
    this.heroList = heroList
  }

  setKiwiAugments(kiwiAugments: GtimgKiwiAugments[] | null) {
    this.kiwiAugments = kiwiAugments
  }

  constructor() {
    makeAutoObservable(this, {
      heroList: observable.ref,
      kiwiAugments: observable.ref
    })
  }
}

export class ExtraAssetsStateFandom {
  balance: Record<string, BalanceType> | null

  setBalance(balance: Record<string, BalanceType> | null) {
    this.balance = balance
  }

  constructor() {
    makeAutoObservable(this, {
      balance: observable.ref
    })
  }
}

export class ExtraAssetsStateOpgg {
  balance: Record<number, OpggAramBalanceItem> = {}
  lastUpdate = 0
  cached = true

  setBalance(balance: Record<number, OpggAramBalanceItem>, lastUpdate: number, cached = false) {
    this.balance = balance
    this.lastUpdate = lastUpdate
    this.cached = cached
  }

  constructor() {
    makeAutoObservable(this, { balance: observable.ref })
  }
}
