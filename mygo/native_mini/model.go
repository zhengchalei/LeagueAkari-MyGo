package nativemini

import (
	"context"
	"errors"
	"fmt"
	"math"
	"sort"
	"strconv"
	"sync"
	"time"

	"github.com/zhengchalei/LeagueAkari-MyGo/mygo/internal/client"
)

type Inputs struct {
	Client             map[string]any
	Kiwi, Fandom, OPGG map[string]any
	ShowSkins          bool
}
type ChampionChoice struct {
	ID                int
	Name, IconPath    string
	Selected, Enabled bool
	Buffs, Nerfs      int
	Balance           []BalanceRow
	Notes             []string
	BalanceKnown      bool
}
type SkinChoice struct {
	ID                int
	Name, ImagePath   string
	Selected, Enabled bool
}
type BalanceRow struct{ Type, Name, Value, Original, Effect string }
type Snapshot struct {
	Connected                        bool
	Phase                            string
	ChampionID                       int
	ChampionName, IconPath           string
	Choices                          []ChampionChoice
	Balance                          []BalanceRow
	BalanceKnown                     bool
	Notes                            []string
	Source, SourceURL, Version       string
	Cached                           bool
	Skins                            []SkinChoice
	SkinsLoading                     bool
	SkinsLoadFailed, SkinApplyFailed bool
	PendingSkinID                    int
	PendingSkinName                  string
	Busy                             bool
	Status, Error                    string
	CanSelectSkin                    bool
	ShowSkins                        bool
	SelectedSkinID, Rerolls          int
	CanReroll                        bool
	ShowReroll                       bool
}
type Request func(context.Context, string, string, any) (any, error)

// Model owns the client-confirmed selection; writes never optimistically select cards.
type Model struct {
	mu               sync.Mutex
	request          Request
	input            Inputs
	snapshot         Snapshot
	skins            []SkinChoice
	generation       uint64
	loadedHero       int
	busy             bool
	selectionEpoch   uint64
	selectionKey     string
	actionGeneration uint64
	actionEpoch      uint64
	actionSkin       bool
	OnChange         func()
}

func New(request Request) *Model { return &Model{request: request} }
func (m *Model) Snapshot() Snapshot {
	m.mu.Lock()
	defer m.mu.Unlock()
	s := m.snapshot
	s.Choices = append([]ChampionChoice(nil), s.Choices...)
	for i := range s.Choices {
		s.Choices[i].Balance = append([]BalanceRow(nil), s.Choices[i].Balance...)
		s.Choices[i].Notes = append([]string(nil), s.Choices[i].Notes...)
	}
	s.Balance = append([]BalanceRow(nil), s.Balance...)
	s.Notes = append([]string(nil), s.Notes...)
	s.Skins = append([]SkinChoice(nil), s.Skins...)
	return s
}
func (m *Model) changed() {
	if m.OnChange != nil {
		m.OnChange()
	}
}
func (m *Model) Refresh(ctx context.Context, in Inputs) {
	m.mu.Lock()
	next := Build(in)
	key := selectionKey(in)
	if key != m.selectionKey {
		m.selectionKey = key
		m.selectionEpoch++
	}
	// Skin data belongs to one visible champion-selection session, even if the
	// client briefly retains currentChampion after leaving that session.
	hero := 0
	if next.Connected && next.Phase == "ChampSelect" && in.ShowSkins {
		hero = next.ChampionID
	}
	changedHero := hero != m.loadedHero
	m.input = in
	if changedHero {
		m.loadedHero = hero
		m.generation++
		m.skins = nil
	}
	next.Error = m.snapshot.Error
	next.SkinsLoadFailed = m.snapshot.SkinsLoadFailed
	next.SkinApplyFailed = m.snapshot.SkinApplyFailed
	next.PendingSkinID, next.PendingSkinName = m.snapshot.PendingSkinID, m.snapshot.PendingSkinName
	next.Busy = m.busy
	next.SkinsLoading = m.snapshot.SkinsLoading
	if changedHero {
		next.Error = ""
		next.SkinsLoadFailed, next.SkinApplyFailed = false, false
		next.PendingSkinID, next.PendingSkinName = 0, ""
		next.SkinsLoading = hero > 0 && m.request != nil
	}
	for _, skin := range m.skins {
		skin.Selected = skin.ID == next.SelectedSkinID
		skin.Enabled = next.CanSelectSkin && !m.busy
		next.Skins = append(next.Skins, skin)
	}
	if hero == 0 {
		next.Skins = nil
		next.SkinsLoading = false
	}
	if m.busy {
		for i := range next.Choices {
			next.Choices[i].Enabled = false
		}
		next.CanReroll = false
	}
	m.snapshot = next
	generation := m.generation
	m.mu.Unlock()
	m.changed()
	if changedHero && hero > 0 && m.request != nil {
		go m.loadSkins(ctx, hero, generation)
	}
}
func (m *Model) loadSkins(ctx context.Context, hero int, generation uint64) {
	ctx, cancel := context.WithTimeout(ctx, 10*time.Second)
	defer cancel()
	carousel, err := m.request(ctx, "GET", "/lol-champ-select/v1/skin-carousel-skins", nil)
	details, _ := m.request(ctx, "GET", fmt.Sprintf("/lol-game-data/assets/v1/champions/%d.json", hero), nil)
	skins := OwnedSkins(hero, carousel, details)
	m.mu.Lock()
	if generation != m.generation || hero != m.loadedHero || !m.snapshot.ShowSkins || !m.snapshot.Connected || m.snapshot.Phase != "ChampSelect" {
		m.mu.Unlock()
		return
	}
	m.skins = skins
	m.snapshot.SkinsLoading = false
	if err != nil {
		m.snapshot.SkinsLoadFailed = true
		m.snapshot.Error = "皮肤加载失败：" + err.Error()
	}
	m.snapshot.Skins = nil
	for _, skin := range skins {
		skin.Selected = skin.ID == m.snapshot.SelectedSkinID
		skin.Enabled = m.snapshot.CanSelectSkin && !m.busy
		m.snapshot.Skins = append(m.snapshot.Skins, skin)
	}
	m.mu.Unlock()
	m.changed()
}
func OwnedSkins(hero int, carousel, details any) []SkinChoice {
	names := map[int]string{}
	for _, raw := range client.List(client.Map(details)["skins"]) {
		skin := client.Map(raw)
		names[num(skin["id"])] = client.String(skin["name"])
		for _, raw := range client.List(skin["chromas"]) {
			chroma := client.Map(raw)
			names[num(chroma["id"])] = client.String(chroma["name"])
		}
	}
	var result []SkinChoice
	seen := map[int]bool{}
	add := func(skin map[string]any, chroma bool) {
		id := num(skin["id"])
		if !flag(skin["unlocked"]) || flag(skin["disabled"]) || seen[id] {
			return
		}
		seen[id] = true
		name := names[id]
		if name == "" {
			name = client.String(skin["name"])
		}
		path := client.String(skin["splashPath"])
		if path == "" {
			path = client.String(skin["tilePath"])
		}
		if chroma && client.String(skin["chromaPreviewPath"]) != "" {
			path = client.String(skin["chromaPreviewPath"])
		}
		result = append(result, SkinChoice{ID: id, Name: name, ImagePath: path})
	}
	for _, raw := range client.List(carousel) {
		skin := client.Map(raw)
		if num(skin["championId"]) != hero || !flag(skin["unlocked"]) {
			continue
		}
		add(skin, false)
		for _, raw := range client.List(skin["childSkins"]) {
			add(client.Map(raw), true)
		}
	}
	return result
}
func Build(in Inputs) Snapshot {
	cs := client.Map(in.Client["champSelect"])
	session := client.Map(cs["session"])
	flow := client.Map(in.Client["gameflow"])
	s := Snapshot{Connected: client.String(client.Map(in.Client["state"])["connectionState"]) == "connected", Phase: client.String(flow["phase"]), ChampionID: num(cs["currentChampion"])}
	s.ShowSkins = in.ShowSkins
	info := client.Map(cs["skinSelectorInfo"])
	s.SelectedSkinID = num(info["selectedSkinId"])
	s.CanSelectSkin = in.ShowSkins && s.Connected && s.Phase == "ChampSelect" && s.ChampionID > 0 && flag(session["allowSkinSelection"]) && !flag(session["isSpectating"]) && !flag(info["skinSelectionDisabled"])
	s.ChampionName, s.IconPath = champion(in, s.ChampionID)
	phase := client.String(client.Map(session["timer"])["phase"])
	enabled := s.Connected && s.Phase == "ChampSelect" && flag(session["benchEnabled"]) && !flag(session["isSpectating"]) && (phase == "FINALIZATION" || (phase == "BAN_PICK" && flag(session["allowSubsetChampionPicks"])))
	subset := client.List(client.Map(client.Map(in.Client["lobbyTeamBuilder"])["champSelect"])["subsetChampionList"])
	ids := []int{}
	if s.ChampionID > 0 {
		ids = append(ids, s.ChampionID)
	}
	if flag(session["benchEnabled"]) {
		bench := client.List(session["benchChampions"])
		benchIDs := []any{}
		for _, raw := range bench {
			benchIDs = append(benchIDs, client.Map(raw)["championId"])
		}
		if phase == "BAN_PICK" {
			for _, id := range subset {
				if !contains(benchIDs, num(id)) && num(id) != s.ChampionID {
					ids = append(ids, num(id))
				}
			}
		}
		for _, raw := range client.List(session["benchChampions"]) {
			ids = append(ids, num(client.Map(raw)["championId"]))
		}
	}
	seen := map[int]bool{}
	for _, id := range ids {
		if id <= 0 || seen[id] {
			continue
		}
		seen[id] = true
		name, path := champion(in, id)
		balance := balanceFor(in, id)
		choice := ChampionChoice{ID: id, Name: name, IconPath: path, Selected: id == s.ChampionID, Enabled: enabled && (id == s.ChampionID || (contains(cs["currentPickableChampionIds"], id) && (phase != "BAN_PICK" || contains(subset, id))))}
		choice.Balance, choice.Notes, choice.BalanceKnown = balance.Balance, balance.Notes, balance.BalanceKnown
		for _, row := range balance.Balance {
			if row.Effect == "buffed" {
				choice.Buffs++
			}
			if row.Effect == "nerfed" {
				choice.Nerfs++
			}
		}
		s.Choices = append(s.Choices, choice)
	}
	b := balanceFor(in, s.ChampionID)
	s.Balance = b.Balance
	s.Notes = b.Notes
	s.BalanceKnown = b.BalanceKnown
	s.Source = b.Source
	s.SourceURL = b.SourceURL
	s.Version = b.Version
	s.Cached = b.Cached
	s.ShowReroll = s.Connected && s.Phase == "ChampSelect" && flag(session["benchEnabled"]) && (num(session["rerollsRemaining"]) > 0 || (flag(session["allowRerolling"]) && phase == "FINALIZATION" && !flag(session["allowSubsetChampionPicks"])))
	if enabled {
		s.Rerolls = num(session["rerollsRemaining"])
	}
	s.CanReroll = enabled && s.Rerolls > 0
	if !s.Connected {
		s.Status = "等待连接英雄联盟客户端"
	} else if s.Phase != "ChampSelect" {
		s.Status = "等待进入英雄选择"
	} else if s.ChampionID == 0 {
		s.Status = "选择你的英雄"
	} else {
		s.Status = "英雄已选择"
	}
	return s
}
func (m *Model) ChooseChampion(ctx context.Context, id int, complete bool) error {
	m.mu.Lock()
	s := m.snapshot
	in := m.input
	if m.busy || id == s.ChampionID {
		m.mu.Unlock()
		return nil
	}
	legal := false
	for _, choice := range s.Choices {
		if choice.ID == id && choice.Enabled {
			legal = true
		}
	}
	if !legal {
		m.mu.Unlock()
		return errors.New("当前不能选择这个英雄")
	}
	session := client.Map(client.Map(in.Client["champSelect"])["session"])
	path := fmt.Sprintf("/lol-champ-select/v1/session/bench/swap/%d", id)
	method := "POST"
	var body any
	if client.String(client.Map(session["timer"])["phase"]) == "BAN_PICK" && s.ChampionID == 0 {
		actionID := -1
	findAction:
		for _, group := range client.List(session["actions"]) {
			for _, raw := range client.List(group) {
				a := client.Map(raw)
				if client.String(a["type"]) == "pick" && !flag(a["completed"]) && num(a["actorCellId"]) == num(session["localPlayerCellId"]) {
					actionID = num(a["id"])
					break findAction
				}
			}
		}
		if actionID < 0 {
			m.mu.Unlock()
			return errors.New("没有可执行的英雄选择")
		}
		method = "PATCH"
		path = fmt.Sprintf("/lol-champ-select/v1/session/actions/%d", actionID)
		body = map[string]any{"championId": id, "completed": complete, "type": "pick"}
	}
	m.startActionLocked(0, "")
	m.mu.Unlock()
	m.changed()
	return m.execute(ctx, method, path, body)
}
func (m *Model) ChooseSkin(ctx context.Context, id int) error {
	m.mu.Lock()
	if m.busy || m.snapshot.SkinsLoading || !m.snapshot.CanSelectSkin {
		m.mu.Unlock()
		return errors.New("当前不能切换皮肤")
	}
	owned := false
	name := ""
	for _, skin := range m.skins {
		if skin.ID == id {
			owned = true
			name = skin.Name
		}
	}
	if !owned {
		m.mu.Unlock()
		return errors.New("皮肤未拥有或不可用")
	}
	if id == m.snapshot.SelectedSkinID {
		m.mu.Unlock()
		return nil
	}
	m.startActionLocked(id, name)
	m.mu.Unlock()
	m.changed()
	return m.execute(ctx, "PATCH", "/lol-champ-select/v1/session/my-selection", map[string]any{"selectedSkinId": id})
}
func (m *Model) Reroll(ctx context.Context, grabBack bool) error {
	m.mu.Lock()
	hero := m.snapshot.ChampionID
	if m.busy || !m.snapshot.CanReroll {
		m.mu.Unlock()
		return errors.New("当前不能重新随机")
	}
	epoch := m.selectionEpoch
	m.startActionLocked(0, "")
	m.mu.Unlock()
	m.changed()
	// Keep the operation reserved until grabbing back finishes; a second click
	// must not overwrite the reroll while its updated bench is being read.
	return m.executeOperation(ctx, func() error {
		_, err := m.request(ctx, "POST", "/lol-champ-select/v1/session/my-selection/reroll", nil)
		if err == nil && grabBack && hero > 0 {
			select {
			case <-ctx.Done():
				return ctx.Err()
			case <-time.After(25 * time.Millisecond):
			}
			// Re-read the live bench instead of swapping using the pre-reroll snapshot.
			fresh, e := m.request(ctx, "GET", "/lol-champ-select/v1/session", nil)
			if e != nil {
				return e
			}
			if ctx.Err() != nil {
				return ctx.Err()
			}
			m.mu.Lock()
			if epoch != m.selectionEpoch || m.selectionKey == "" {
				m.mu.Unlock()
				return errors.New("英雄选择已结束，取消抢回英雄")
			}
			in := m.input
			copy := client.Clone(in.Client).(map[string]any)
			cs := client.Map(copy["champSelect"])
			cs["session"] = fresh
			for _, raw := range client.List(client.Map(fresh)["myTeam"]) {
				p := client.Map(raw)
				if num(p["cellId"]) == num(client.Map(fresh)["localPlayerCellId"]) {
					cs["currentChampion"] = p["championId"]
				}
			}
			in.Client = copy
			if selectionKey(in) != m.selectionKey {
				m.mu.Unlock()
				return errors.New("英雄选择会话已切换，取消抢回英雄")
			}
			freshSnapshot := Build(in)
			legal := false
			for _, choice := range freshSnapshot.Choices {
				if choice.ID == hero && choice.Enabled {
					legal = true
				}
			}
			m.mu.Unlock()
			if freshSnapshot.ChampionID == hero {
				return nil
			}
			if !legal {
				return errors.New("原英雄已不在可用备选席，无法抢回")
			}
			// Do not publish a separately fetched session over the event stream's
			// newer state. Only the confirmed client state selects the hero card.
			_, err = m.request(ctx, "POST", fmt.Sprintf("/lol-champ-select/v1/session/bench/swap/%d", hero), nil)
		}
		return err
	})
}
func (m *Model) execute(ctx context.Context, method, path string, body any) error {
	return m.executeOperation(ctx, func() error {
		_, err := m.request(ctx, method, path, body)
		return err
	})
}
func (m *Model) executeOperation(ctx context.Context, operation func() error) error {
	m.mu.Lock()
	generation := m.actionGeneration
	epoch := m.actionEpoch
	skinAction := m.actionSkin
	stale := epoch != m.selectionEpoch || (skinAction && generation != m.generation)
	m.mu.Unlock()
	var err error
	if stale {
		err = errors.New("英雄选择已改变，取消当前操作")
	} else if m.request == nil {
		err = errors.New("客户端请求不可用")
	} else if ctx.Err() != nil {
		err = ctx.Err()
	} else {
		err = operation()
	}
	m.mu.Lock()
	m.busy = false
	m.snapshot.Busy = false
	if err != nil && generation == m.generation && epoch == m.selectionEpoch {
		m.snapshot.Error = err.Error()
		m.snapshot.SkinApplyFailed = skinAction
	}
	m.snapshot.PendingSkinID, m.snapshot.PendingSkinName = 0, ""
	m.restoreAvailabilityLocked()
	m.mu.Unlock()
	m.changed()
	return err
}
func (m *Model) startActionLocked(skinID int, name string) {
	m.actionGeneration = m.generation
	m.actionEpoch = m.selectionEpoch
	m.actionSkin = skinID > 0
	m.busy, m.snapshot.Busy = true, true
	m.snapshot.Error = ""
	m.snapshot.SkinApplyFailed = false
	m.snapshot.PendingSkinID, m.snapshot.PendingSkinName = skinID, name
	m.restoreAvailabilityLocked()
}
func (m *Model) restoreAvailabilityLocked() {
	available := Build(m.input)
	choices := map[int]bool{}
	for _, choice := range available.Choices {
		choices[choice.ID] = choice.Enabled
	}
	for i := range m.snapshot.Choices {
		m.snapshot.Choices[i].Enabled = !m.busy && choices[m.snapshot.Choices[i].ID]
	}
	m.snapshot.CanReroll = !m.busy && available.CanReroll
	for i := range m.snapshot.Skins {
		m.snapshot.Skins[i].Enabled = !m.busy && m.snapshot.CanSelectSkin
	}
}
func selectionKey(in Inputs) string {
	if client.String(client.Map(in.Client["state"])["connectionState"]) != "connected" || client.String(client.Map(in.Client["gameflow"])["phase"]) != "ChampSelect" {
		return ""
	}
	session := client.Map(client.Map(in.Client["champSelect"])["session"])
	if len(session) == 0 {
		return ""
	}
	return fmt.Sprintf("%v:%v", session["gameId"], session["localPlayerCellId"])
}
func champion(in Inputs, id int) (string, string) {
	if id <= 0 {
		return "选择英雄", ""
	}
	c := client.Map(client.Map(client.Map(in.Client["gameData"])["champions"])[strconv.Itoa(id)])
	name := client.String(c["name"])
	if name == "" {
		name = fmt.Sprintf("英雄 %d", id)
	}
	path := client.String(c["squarePortraitPath"])
	if path == "" {
		path = fmt.Sprintf("/lol-game-data/assets/v1/champion-icons/%d.png", id)
	}
	return name, path
}
func num(v any) int { return int(number(v)) }
func number(v any) float64 {
	switch v := v.(type) {
	case float64:
		return v
	case int:
		return float64(v)
	case int64:
		return float64(v)
	case float32:
		return float64(v)
	}
	return 0
}
func flag(v any) bool { b, _ := v.(bool); return b }
func contains(value any, id int) bool {
	for _, v := range client.List(value) {
		if num(v) == id {
			return true
		}
	}
	return false
}
func balanceFor(in Inputs, id int) Snapshot {
	s := Snapshot{}
	if id <= 0 {
		return s
	}
	gameData := client.Map(client.Map(client.Map(in.Client["gameflow"])["session"])["gameData"])
	mode := client.String(client.Map(gameData["queue"])["gameMode"])
	if mode == "" {
		mode = client.String(gameData["gameMode"])
	}
	var adjustments []any
	if mode == "KIWI" {
		record := client.Map(client.Map(in.Kiwi["balance"])[strconv.Itoa(id)])
		s.BalanceKnown = len(record) > 0
		s.Source = "Bilibili RESG"
		s.SourceURL = client.String(in.Kiwi["sourceUrl"])
		s.Version = client.String(in.Kiwi["version"])
		s.Cached = flag(in.Kiwi["cached"])
		adjustments = client.List(record["adjustments"])
	} else if mode == "ARAM" {
		record := client.Map(client.Map(in.OPGG["balance"])[strconv.Itoa(id)])
		s.BalanceKnown = len(record) > 0
		s.Source = "OP.GG"
		fields := [][2]string{{"damage_dealt", "damage-dealt"}, {"damage_taken", "damage-taken"}, {"attack_speed", "attack-speed"}, {"cooldown_reduction", "ability-haste"}, {"healing", "healing"}, {"tenacity", "tenacity"}, {"shield_amount", "shielding"}, {"energy_regen", "energy-regen"}, {"area_of_effect_damage", "area-of-effect-damage"}}
		for _, f := range fields {
			raw, exists := record[f[0]]
			if !exists {
				continue
			}
			value := number(raw)
			display := "percentage"
			baseline := 100.0
			if f[0] == "cooldown_reduction" || f[0] == "tenacity" {
				display = "literal"
				baseline = 0
			}
			if value == baseline {
				continue
			}
			if display == "percentage" {
				value /= 100
			}
			adjustments = append(adjustments, normalized(f[1], value, display))
		}
	} else {
		record := client.Map(client.Map(in.Fandom["balance"])[strconv.Itoa(id)])
		modeKey := map[string]string{"URF": "urf", "ONEFORALL": "ofa", "ULTBOOK": "usb", "NEXUSBLITZ": "nb", "CHERRY": "ar"}[mode]
		values := client.Map(client.Map(record["balance"])[modeKey])
		s.BalanceKnown = len(values) > 0
		s.Source = "Fandom Wiki"
		fields := map[string]string{"dmg_dealt": "damage-dealt", "dmg_taken": "damage-taken", "shielding": "shielding", "healing": "healing", "ability_haste": "ability-haste", "attack_speed": "attack-speed", "energy_regen": "energy-regen", "tenacity": "tenacity", "movement_speed": "movement-speed"}
		for key, raw := range values {
			kind := fields[key]
			if kind == "" {
				continue
			}
			display := "percentage"
			base := 1.0
			if kind == "ability-haste" {
				display = "literal"
				base = 0
			}
			if number(raw) == base {
				continue
			}
			adjustments = append(adjustments, normalized(kind, number(raw), display))
		}
	}
	labels := map[string]string{"damage-dealt": "造成伤害", "damage-taken": "承受伤害", "healing": "治疗效果", "shielding": "护盾效果", "ability-haste": "技能急速", "attack-speed": "攻击速度", "attack-speed-growth": "攻速成长", "resource-regen": "资源回复", "energy-regen": "能量回复", "movement-speed": "移动速度", "tenacity": "韧性", "area-of-effect-damage": "范围伤害"}
	for _, raw := range adjustments {
		a := client.Map(raw)
		kind := client.String(a["type"])
		if kind == "special" {
			if note := client.String(a["description"]); note != "" {
				s.Notes = append(s.Notes, note)
			}
			continue
		}
		value := number(a["value"])
		original := client.String(a["formattedValue"])
		percent := client.String(a["display"]) == "percentage"
		change := value
		if percent {
			change = (value - 1) * 100
		}
		text := signed(change)
		if percent {
			text += "%"
		}
		if kind == "attack-speed-growth" && original != "" {
			text = original
		}
		if original == "" {
			original = signed(value)
			if percent {
				original = strconv.FormatFloat(value*100, 'f', -1, 64) + "%"
			}
		}
		name := labels[kind]
		if name == "" {
			name = kind
		}
		s.Balance = append(s.Balance, BalanceRow{Type: kind, Name: name, Value: text, Original: original, Effect: client.String(a["effect"])})
	}
	order := map[string]int{"damage-dealt": 0, "damage-taken": 1, "healing": 2, "shielding": 3, "ability-haste": 4, "attack-speed": 5, "attack-speed-growth": 5, "resource-regen": 6, "energy-regen": 6, "movement-speed": 7, "tenacity": 8}
	effect := map[string]int{"buffed": 0, "nerfed": 1, "neutral": 2}
	sort.SliceStable(s.Balance, func(i, j int) bool {
		a, b := s.Balance[i], s.Balance[j]
		if effect[a.Effect] != effect[b.Effect] {
			return effect[a.Effect] < effect[b.Effect]
		}
		return order[a.Type] < order[b.Type]
	})
	return s
}
func normalized(kind string, value float64, display string) any {
	base := 0.0
	if display == "percentage" {
		base = 1
	}
	buff := value > base
	if kind == "damage-taken" {
		buff = !buff
	}
	effect := "nerfed"
	if buff {
		effect = "buffed"
	}
	return map[string]any{"type": kind, "value": value, "display": display, "effect": effect}
}
func signed(value float64) string {
	value = math.Round(value*100) / 100
	prefix := ""
	if value > 0 {
		prefix = "+"
	}
	if value < 0 {
		prefix = "−"
	}
	return prefix + strconv.FormatFloat(math.Abs(value), 'f', -1, 64)
}
