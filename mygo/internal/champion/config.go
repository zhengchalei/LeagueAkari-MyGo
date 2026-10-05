package champion

import (
	"encoding/json"
	"errors"
	"fmt"
	"strconv"
	"strings"

	"github.com/zhengchalei/LeagueAkari-MyGo/mygo/internal/settings"
)

const Namespace = "auto-champ-config-main"

type RunesConfig struct {
	PrimaryStyleID  int   `json:"primaryStyleId"`
	SubStyleID      int   `json:"subStyleId"`
	SelectedPerkIDs []int `json:"selectedPerkIds"`
}

type SpellsConfig struct {
	Spell1ID int `json:"spell1Id"`
	Spell2ID int `json:"spell2Id"`
}

type Config struct {
	Enabled        bool                                `json:"enabled"`
	Runes          map[string]map[string]*RunesConfig  `json:"runesV2"`
	SummonerSpells map[string]map[string]*SpellsConfig `json:"summonerSpells"`
}

func Defaults() map[string]any {
	return map[string]any{"enabled": false, "runesV2": map[string]any{}, "summonerSpells": map[string]any{}}
}

// UpdateRunes persists the renderer's existing hero/mode key, including null clears.
func (r *Runner) UpdateRunes(championID int, key string, value any) error {
	var config *RunesConfig
	if err := decode(value, &config); err != nil {
		return fmt.Errorf("符文配置格式错误: %w", err)
	}
	if config != nil {
		if err := config.validate(); err != nil {
			return err
		}
	}
	return r.update("runesV2", championID, key, config)
}

func (r *Runner) UpdateSpells(championID int, key string, value any) error {
	var config *SpellsConfig
	if err := decode(value, &config); err != nil {
		return fmt.Errorf("召唤师技能配置格式错误: %w", err)
	}
	if config != nil {
		if err := config.validate(); err != nil {
			return err
		}
	}
	return r.update("summonerSpells", championID, key, config)
}

func (r *Runner) UpdateSummonerSpells(championID int, key string, value any) error {
	return r.UpdateSpells(championID, key, value)
}

func (r *Runner) update(field string, championID int, key string, value any) error {
	if championID <= 0 || strings.TrimSpace(key) == "" {
		return errors.New("英雄 ID 和配置类型不能为空")
	}
	r.updateMu.Lock()
	defer r.updateMu.Unlock()
	entries, _ := r.settings.Get(Namespace, field).(map[string]any)
	if entries == nil {
		entries = map[string]any{}
	}
	id := strconv.Itoa(championID)
	hero, _ := entries[id].(map[string]any)
	if hero == nil {
		hero = map[string]any{}
	}
	hero[key] = value
	entries[id] = hero
	return r.settings.Set(Namespace, field, entries)
}

func (c *RunesConfig) validate() error {
	if c.PrimaryStyleID <= 0 || c.SubStyleID <= 0 || c.PrimaryStyleID == c.SubStyleID || len(c.SelectedPerkIDs) == 0 {
		return errors.New("符文配置必须包含主系、副系和已选符文")
	}
	for _, id := range c.SelectedPerkIDs {
		if id <= 0 {
			return errors.New("符文 ID 必须大于零")
		}
	}
	return nil
}

func (c *SpellsConfig) validate() error {
	if c.Spell1ID <= 0 || c.Spell2ID <= 0 || c.Spell1ID == c.Spell2ID {
		return errors.New("请选择两个不同的召唤师技能")
	}
	return nil
}

func decode(value, target any) error {
	data, err := json.Marshal(value)
	if err != nil {
		return err
	}
	return json.Unmarshal(data, target)
}

func loadConfig(store *settings.Store) (Config, error) {
	var config Config
	err := store.Decode(Namespace, &config)
	return config, err
}
