package champion

import (
	"context"
	"errors"
	"fmt"
	"net/http"
	"strconv"
)

type champSession struct {
	GameID            int64 `json:"gameId"`
	LocalPlayerCellID int   `json:"localPlayerCellId"`
	MyTeam            []struct {
		CellID           int    `json:"cellId"`
		ChampionID       int    `json:"championId"`
		AssignedPosition string `json:"assignedPosition"`
	} `json:"myTeam"`
}

type gameSession struct {
	GameData struct {
		GameID int64 `json:"gameId"`
		Queue  struct {
			GameMode string `json:"gameMode"`
			Type     string `json:"type"`
		} `json:"queue"`
	} `json:"gameData"`
}

func (r *Runner) currentSelection(ctx context.Context) (*selection, error) {
	var phase string
	var game gameSession
	var champions champSession
	if client, ok := r.client.(gameplayClient); ok {
		state := client.GameplayState()
		gameflow, _ := state["gameflow"].(map[string]any)
		champSelect, _ := state["champSelect"].(map[string]any)
		phase, _ = gameflow["phase"].(string)
		if phase != "ChampSelect" || gameflow["session"] == nil || champSelect["session"] == nil {
			return nil, nil
		}
		if err := decode(gameflow["session"], &game); err != nil {
			return nil, err
		}
		if err := decode(champSelect["session"], &champions); err != nil {
			return nil, err
		}
	} else {
		if err := r.get(ctx, "/lol-gameflow/v1/gameflow-phase", &phase); err != nil {
			return nil, err
		}
		if phase != "ChampSelect" {
			return nil, nil
		}
		if err := r.get(ctx, "/lol-gameflow/v1/session", &game); err != nil {
			return nil, err
		}
		if err := r.get(ctx, "/lol-champ-select/v1/session", &champions); err != nil {
			return nil, err
		}
	}
	for _, player := range champions.MyTeam {
		if player.CellID == champions.LocalPlayerCellID {
			id := game.GameData.GameID
			if id == 0 {
				id = champions.GameID
			}
			return &selection{ChampionID: player.ChampionID, CellID: player.CellID, GameID: id,
				Position: player.AssignedPosition, Mode: game.GameData.Queue.GameMode, QueueType: game.GameData.Queue.Type}, nil
		}
	}
	return nil, nil
}

func (r *Runner) get(ctx context.Context, path string, target any) error {
	value, err := r.client.JSON(ctx, http.MethodGet, path, nil)
	if err != nil {
		return err
	}
	return decode(value, target)
}

// Recheck the live player before each write: switching a hero or leaving champ
// select must not complete an older rune/spell application.
func (r *Runner) guard(ctx context.Context, expected selection) error {
	if err := ctx.Err(); err != nil {
		return err
	}
	var phase string
	if err := r.get(ctx, "/lol-gameflow/v1/gameflow-phase", &phase); err != nil {
		return err
	}
	if phase != "ChampSelect" {
		return context.Canceled
	}
	var session champSession
	if err := r.get(ctx, "/lol-champ-select/v1/session", &session); err != nil {
		return err
	}
	if session.GameID != 0 && expected.GameID != 0 && session.GameID != expected.GameID {
		return context.Canceled
	}
	for _, player := range session.MyTeam {
		if player.CellID == session.LocalPlayerCellID {
			if player.CellID == expected.CellID && player.ChampionID == expected.ChampionID && player.AssignedPosition == expected.Position {
				return ctx.Err()
			}
			break
		}
	}
	return context.Canceled
}

func (r *Runner) write(ctx context.Context, self selection, method, path string, body any) error {
	if err := r.guard(ctx, self); err != nil {
		return err
	}
	_, err := r.client.JSON(ctx, method, path, body)
	return err
}

type perkPage struct {
	ID         int    `json:"id"`
	Name       string `json:"name"`
	IsEditable bool   `json:"isEditable"`
	Current    bool   `json:"current"`
}

var errNoRunePages = errors.New("no rune pages available")

func (r *Runner) applyRunes(ctx context.Context, self selection, config *RunesConfig) error {
	var inventory struct {
		CanAddCustomPage bool `json:"canAddCustomPage"`
	}
	if err := r.get(ctx, "/lol-perks/v1/inventory", &inventory); err != nil {
		return err
	}
	pageID := 0
	name := r.pageName(self)
	if inventory.CanAddCustomPage {
		if err := r.guard(ctx, self); err != nil {
			return err
		}
		added, err := r.client.JSON(ctx, http.MethodPost, "/lol-perks/v1/pages/", map[string]any{
			"name": name, "isEditable": true, "primaryStyleId": strconv.Itoa(config.PrimaryStyleID),
		})
		if err != nil {
			return err
		}
		var page perkPage
		if err := decode(added, &page); err != nil {
			return err
		}
		pageID = page.ID
	} else {
		var pages []perkPage
		if err := r.get(ctx, "/lol-perks/v1/pages", &pages); err != nil {
			return err
		}
		if len(pages) > 0 {
			pageID = pages[0].ID
		} else {
			return errNoRunePages
		}
	}
	body := map[string]any{"id": pageID, "name": name,
		"isRecommendationOverride": false, "isTemporary": false,
		"primaryStyleId": config.PrimaryStyleID, "subStyleId": config.SubStyleID, "selectedPerkIds": config.SelectedPerkIDs}
	if err := r.write(ctx, self, http.MethodPut, fmt.Sprintf("/lol-perks/v1/pages/%d", pageID), body); err != nil {
		return err
	}
	return r.write(ctx, self, http.MethodPut, "/lol-perks/v1/currentpage", pageID)
}

func (r *Runner) pageName(self selection) string {
	championName := strconv.Itoa(self.ChampionID)
	if client, ok := r.client.(interface{ State() map[string]any }); ok {
		data, _ := client.State()["gameData"].(map[string]any)
		champions, _ := data["champions"].(map[string]any)
		champion, _ := champions[championName].(map[string]any)
		if name, _ := champion["name"].(string); name != "" {
			championName = name
		}
	}
	name := "[Akari] " + championName
	if self.Position != "" {
		position := map[string]string{"top": "上路", "jungle": "打野", "middle": "中路", "bottom": "下路", "utility": "辅助"}[self.Position]
		if r.english() {
			position = map[string]string{"top": "Top", "jungle": "Jungle", "middle": "Middle", "bottom": "Bottom", "utility": "Support"}[self.Position]
		}
		if position == "" {
			position = self.Position
		}
		name += " - " + position
	}
	return name
}
