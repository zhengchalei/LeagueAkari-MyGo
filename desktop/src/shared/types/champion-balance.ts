export interface BalanceAdjustment {
  /** 该增益 / 减益的类型 */
  type:
    | 'damage-dealt'
    | 'damage-taken'
    | 'shielding'
    | 'healing'
    | 'ability-haste'
    | 'attack-speed'
    | 'attack-speed-growth'
    | 'energy-regen'
    | 'mana-regen'
    | 'resource-regen'
    | 'tenacity'
    | 'movement-speed'
    | 'area-of-effect-damage'
    | 'special'

  value: number
  display: 'percentage' | 'literal'

  /** 数值上升对应的效果；承受伤害等属性的方向与伤害输出相反。 */
  effectType: 'buff' | 'nerf' | 'neutral'
  effect: 'buffed' | 'nerfed' | 'neutral'
  description?: string
  formattedValue?: string
}

export interface KiwiChampionBalance {
  id: number
  adjustments: BalanceAdjustment[]
}
