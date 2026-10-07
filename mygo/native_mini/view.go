package nativemini

import (
	"fmt"
	"strings"

	"github.com/egoist/mygo/ui"
)

// Action describes an intent; only the client-confirmed snapshot marks a choice selected.
type Action struct {
	Kind  string
	ID    int
	Value bool
}

type Controls struct {
	Theme                    string
	Locale                   string
	Pinned                   bool
	CanDodge                 bool
	TemporarilyDisabled      bool
	CanAccept                bool
	CanCancel                bool
	CanCancelAutoAccept      bool
	CanCancelAutoMatchmaking bool
	Plans                    []string
}

// View draws directly into MyGo's native surface. Asset must return immediately,
// queuing unavailable images outside the render thread and invalidating on arrival.
type View struct {
	snapshot func() Snapshot
	asset    func(string) *ui.Bitmap
	dispatch func(Action)
	Controls func() Controls
	skinGrid ui.GridState
	skinHero int
}

func NewView(snapshot func() Snapshot, asset func(string) *ui.Bitmap, dispatch func(Action)) *View {
	return &View{snapshot: snapshot, asset: asset, dispatch: dispatch}
}

func (v *View) send(action Action) {
	if v.dispatch != nil {
		v.dispatch(action)
	}
}

func (v *View) Draw(c *ui.Context) {
	s := v.snapshot()
	opts := Controls{}
	if v.Controls != nil {
		opts = v.Controls()
	}
	switch opts.Theme {
	case "dark":
		c.SetTheme(ui.DarkTheme())
	case "light":
		c.SetTheme(ui.LightTheme())
	}
	t := c.Theme()
	en := strings.HasPrefix(opts.Locale, "en")
	tr := func(zh, english string) string {
		if en {
			return english
		}
		return zh
	}
	ui.Scroll(c).Fill().Children(func() {
		ui.Column(c).Padding(10).Gap(8).Children(func() {
			ui.Row(c).Gap(6).Children(func() {
				ui.Text(c, tr("选人助手", "Champion select")).Bold().Grow(1)
				label := tr("置顶", "Pin")
				if opts.Pinned {
					label = tr("已置顶", "Pinned")
				}
				if ui.Button(c, label).Key("pin").Padding(4, 8).Clicked() {
					v.send(Action{Kind: "pin", Value: !opts.Pinned})
				}
			})
			if !s.Connected {
				ui.Text(c, tr("等待英雄联盟客户端连接", "Waiting for League client")).TextColor(t.TextMuted).PaddingY(12)
				return
			}
			v.card(c, func() {
				ui.Row(c).Gap(8).Children(func() {
					v.picture(c, s.IconPath, s.ChampionName, 42, 42)
					ui.Column(c).Grow(1).Gap(2).Children(func() {
						name := s.ChampionName
						if name == "" {
							name = tr("等待选择英雄", "Choose a champion")
						}
						ui.Text(c, name).Bold().FontSize(15)
						ui.Text(c, phaseLabel(s.Phase, en)).FontSize(11).TextColor(t.TextMuted)
					})
				})
				if len(s.Choices) > 0 {
					ui.Grid(c).Columns(min(5, len(s.Choices))).Gap(4).MarginY(8).Children(func() {
						for _, champion := range s.Choices {
							button := ui.Button(c, "").Key(champion.ID).Label(champion.Name).Padding(3).Disabled(!champion.Enabled).Column().Gap(3)
							if champion.Selected {
								button.Border(2, t.Accent)
							}
							button.Children(func() {
								v.picture(c, champion.IconPath, champion.Name, 38, 38).AlignSelf(ui.Center)
								ui.Text(c, champion.Name).FontSize(10).SingleLine().Ellipsis("…").TextAlign(ui.Center)
								if champion.Buffs+champion.Nerfs > 0 {
									ui.Row(c).Gap(3).Justify(ui.Center).Children(func() {
										if champion.Buffs > 0 {
											ui.Textf(c, "+%d", champion.Buffs).FontSize(10).TextColor(t.Success)
										}
										if champion.Nerfs > 0 {
											ui.Textf(c, "−%d", champion.Nerfs).FontSize(10).TextColor(t.Danger)
										}
									})
								}
							})
							if button.Clicked() {
								v.send(Action{Kind: "champion", ID: champion.ID})
							}
						}
					})
				}
				if s.CanReroll || s.Rerolls > 0 {
					ui.Row(c).Gap(4).Children(func() {
						if ui.Button(c, fmt.Sprintf(tr("重随机 (%d)", "Reroll (%d)"), s.Rerolls)).Disabled(!s.CanReroll).FontSize(11).Padding(5, 8).Clicked() {
							v.send(Action{Kind: "reroll"})
						}
						if ui.Button(c, tr("重随机并取回", "Reroll & take back")).Disabled(!s.CanReroll).FontSize(11).Padding(5, 8).Clicked() {
							v.send(Action{Kind: "reroll-grab-back"})
						}
					})
				}
			})
			if s.ChampionID != 0 {
				v.balance(c, s, tr, en)
				if s.ShowSkins {
					v.skins(c, s, tr)
				}
			}
			if len(opts.Plans) > 0 {
				v.card(c, func() {
					for _, plan := range opts.Plans {
						ui.Text(c, plan).FontSize(11)
					}
				})
			}
			if opts.CanAccept || opts.CanCancel || opts.CanDodge || opts.CanCancelAutoAccept || opts.CanCancelAutoMatchmaking {
				v.card(c, func() {
					if opts.CanAccept {
						ui.Row(c).Gap(6).Children(func() {
							if ui.PrimaryButton(c, tr("接受对局", "Accept")).Clicked() {
								v.send(Action{Kind: "accept"})
							}
							if ui.Button(c, tr("拒绝", "Decline")).Clicked() {
								v.send(Action{Kind: "decline"})
							}
						})
					}
					if opts.CanCancel && ui.Button(c, tr("取消匹配", "Cancel queue")).Clicked() {
						v.send(Action{Kind: "cancel-queue"})
					}
					if opts.CanCancelAutoAccept && ui.Button(c, tr("取消自动接受", "Cancel automatic accept")).FontSize(11).Clicked() {
						v.send(Action{Kind: "cancel-auto-accept"})
					}
					if opts.CanCancelAutoMatchmaking && ui.Button(c, tr("取消自动匹配", "Cancel automatic matchmaking")).FontSize(11).Clicked() {
						v.send(Action{Kind: "cancel-auto-matchmaking"})
					}
					if opts.CanDodge {
						ui.Row(c).Gap(6).Children(func() {
							ui.Text(c, tr("退出英雄选择", "Leave champion select")).FontSize(11).Grow(1)
							if ui.Button(c, tr("立即秒退", "Dodge")).FontSize(11).Padding(5, 8).Clicked() {
								v.send(Action{Kind: "dodge"})
							}
						})
						ui.Row(c).Gap(6).Children(func() {
							ui.Text(c, tr("临时取消自动选择/禁用", "Pause automatic pick / ban")).FontSize(11).Grow(1)
							disabled := opts.TemporarilyDisabled
							ui.Switch(c, &disabled).Label("disable-auto")
							if disabled != opts.TemporarilyDisabled {
								v.send(Action{Kind: "disable-auto", Value: disabled})
							}
						})
					}
				})
			}
			if s.Status != "" {
				ui.Text(c, s.Status).FontSize(11).TextColor(t.TextMuted)
			}
			if s.Error != "" {
				ui.Text(c, s.Error).FontSize(11).TextColor(t.Danger)
			}
		})
	})
}

func (v *View) card(c *ui.Context, children func()) {
	t := c.Theme()
	ui.Column(c).Padding(9).Gap(5).Radius(6).Background(t.Surface).Border(1, t.Border).Children(children)
}

func (v *View) picture(c *ui.Context, path, name string, width, height float32) *ui.Element {
	if path != "" && v.asset != nil {
		if image := v.asset(path); image != nil {
			return ui.Image(c, image).Size(width, height).Fit(ui.Cover).Radius(4).Shrink(0)
		}
	}
	return ui.Box(c).Size(width, height).Radius(4).Background(c.Theme().Border).Center().Shrink(0).Children(func() {
		initial := "?"
		if chars := []rune(name); len(chars) > 0 {
			initial = string(chars[0])
		}
		ui.Text(c, initial).FontSize(12).TextColor(c.Theme().TextMuted)
	})
}

func (v *View) balance(c *ui.Context, s Snapshot, tr func(string, string) string, english bool) {
	t := c.Theme()
	v.card(c, func() {
		ui.Text(c, tr("增益 / 减益", "Buffs / nerfs")).Bold().FontSize(12)
		for i, row := range s.Balance {
			color := t.TextMuted
			label := tr("调整", "Change")
			switch row.Effect {
			case "buffed":
				color, label = t.Success, tr("增益", "Buff")
			case "nerfed":
				color, label = t.Danger, tr("减益", "Nerf")
			}
			name := row.Name
			if english {
				if english := balanceLabelsEnglish[row.Type]; english != "" {
					name = english
				}
			}
			ui.Row(c).Key(i).Padding(6, 8).Gap(6).Radius(4).Background(color.Mix(t.Surface, 0.93)).Children(func() {
				ui.Text(c, label).FontSize(10).TextColor(color)
				ui.Text(c, name).FontSize(12).Grow(1)
				ui.Text(c, row.Value).FontSize(21).Bold().TextColor(color).Tooltip(row.Original)
			})
		}
		if len(s.Balance) == 0 {
			message := tr("暂无此模式独立调整数据", "No independent balance data for this mode")
			if s.BalanceKnown {
				message = tr("数据源未列出该英雄的调整", "Source lists no changes for this champion")
			}
			ui.Text(c, message).FontSize(11).TextColor(t.TextMuted)
		}
		for _, note := range s.Notes {
			ui.Text(c, note).FontSize(11).TextColor(t.TextMuted)
		}
		if s.Source != "" {
			label := strings.TrimSpace(s.Source + " " + s.Version)
			if s.Cached {
				label += tr(" · 缓存", " · cached")
			}
			ui.Text(c, label).FontSize(10).TextColor(t.TextMuted).Tooltip(s.SourceURL)
		}
	})
}

var balanceLabelsEnglish = map[string]string{
	"damage-dealt": "Damage dealt", "damage-taken": "Damage taken", "healing": "Healing", "shielding": "Shielding",
	"ability-haste": "Ability haste", "attack-speed": "Attack speed", "attack-speed-growth": "Attack speed growth",
	"resource-regen": "Resource regen", "energy-regen": "Energy regen", "movement-speed": "Movement speed",
	"tenacity": "Tenacity", "area-of-effect-damage": "Area damage",
}

func (v *View) skins(c *ui.Context, s Snapshot, tr func(string, string) string) {
	t := c.Theme()
	v.card(c, func() {
		ui.Text(c, fmt.Sprintf(tr("已有皮肤 (%d)", "Owned skins (%d)"), len(s.Skins))).Bold().FontSize(12)
		if s.SkinsLoading {
			ui.Text(c, tr("正在读取已有皮肤…", "Loading owned skins…")).FontSize(11).TextColor(t.TextMuted)
			return
		}
		if len(s.Skins) == 0 {
			ui.Text(c, tr("等待客户端皮肤数据", "Waiting for client skin data")).FontSize(11).TextColor(t.TextMuted)
			return
		}
		if v.skinHero != s.ChampionID {
			v.skinHero = s.ChampionID
			v.skinGrid = ui.GridState{}
		}
		v.skinGrid.Key = func(i int) any { return s.Skins[i].ID }
		width, _ := c.Size()
		gap := t.Space(2)
		// Reserve the outer padding, card border and scrollbar, keeping three
		// columns as the window grows. GridView only requests visible assets.
		minimumWidth := max(1, (width-40-2*gap-t.ScrollbarWidth)/3)
		height := min(float32(228), float32((len(s.Skins)+2)/3)*(86+gap))
		ui.GridView(c, &v.skinGrid, len(s.Skins), minimumWidth, 86, func(i int) {
			skin := s.Skins[i]
			button := ui.Button(c, "").Key(skin.ID).Label(skin.Name).Disabled(!s.CanSelectSkin || !skin.Enabled).Column().Fill().Padding(3).Gap(4)
			if skin.Selected {
				button.Border(2, t.Accent)
			}
			button.Children(func() {
				v.picture(c, skin.ImagePath, skin.Name, 80, 48).MaxWidthPercent(100).AlignSelf(ui.Center)
				ui.Text(c, skin.Name).FontSize(10).MaxLines(2).TextAlign(ui.Center)
			})
			if button.Clicked() {
				v.send(Action{Kind: "skin", ID: skin.ID})
			}
		}).Label("skin-grid").Height(height).MaxHeight(228)
	})
}

func phaseLabel(phase string, en bool) string {
	labels := map[string][2]string{
		"ChampSelect": {"英雄选择", "Champion select"}, "BAN_PICK": {"英雄选择（进行中）", "Champion select (in progress)"},
		"FINALIZATION": {"英雄选择（已完成）", "Champion select (complete)"}, "PLANNING": {"准备选择", "Preparing selection"},
		"Lobby": {"组队大厅", "Lobby"}, "Matchmaking": {"正在匹配", "Matchmaking"}, "ReadyCheck": {"已找到对局", "Match found"},
		"InProgress": {"对局进行中", "In game"}, "None": {"等待进入选人", "Waiting for champion select"},
	}
	if pair, ok := labels[phase]; ok {
		if en {
			return pair[1]
		}
		return pair[0]
	}
	return phase
}
