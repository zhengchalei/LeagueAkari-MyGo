package automation

import (
	"context"
	"errors"
	"fmt"
	"math/rand/v2"
	"strings"

	"github.com/zhengchalei/LeagueAkari-MyGo/mygo/internal/settings"
)

type EligiblePlayer struct {
	Puuid     string `json:"puuid"`
	BotPlayer bool   `json:"botPlayer"`
}

type HonorBallot struct {
	GameID            int64            `json:"gameId"`
	EligibleAllies    []EligiblePlayer `json:"eligibleAllies"`
	EligibleOpponents []EligiblePlayer `json:"eligibleOpponents"`
	HonoredPlayers    []struct {
		RecipientPuuid string `json:"recipientPuuid"`
	} `json:"honoredPlayers"`
	VotePool struct {
		Votes int `json:"votes"`
	} `json:"votePool"`
}

type honorProgress struct {
	attempted map[string]bool
	completed bool
}

func (runner *Runner) honor(ctx context.Context, initial *HonorBallot, strategy string) error {
	progress := runner.honorGames[initial.GameID]
	if progress == nil {
		progress = &honorProgress{attempted: map[string]bool{}}
		runner.honorGames[initial.GameID] = progress
	}
	if progress.completed {
		return nil
	}
	if !validHonorStrategy(strategy) {
		return fmt.Errorf("unknown honor strategy %q", strategy)
	}
	var summoner struct {
		Puuid string `json:"puuid"`
	}
	if strategy != "opt-out" {
		if err := runner.get(ctx, "/lol-summoner/v1/current-summoner", &summoner); err != nil {
			return err
		}
		if summoner.Puuid == "" {
			return errors.New("current summoner is unavailable for auto-honor")
		}
	}
	var lobbyMembers []string
	if strategy == "prefer-lobby-member" || strategy == "only-lobby-member" || strategy == "all-member-including-opponent" {
		var status struct {
			EogPlayers   []string `json:"eogPlayers"`
			LeftPlayers  []string `json:"leftPlayers"`
			ReadyPlayers []string `json:"readyPlayers"`
		}
		if err := runner.get(ctx, "/lol-lobby/v2/party/eog-status", &status); err != nil {
			if strategy == "only-lobby-member" {
				return err
			}
		} else {
			lobbyMembers = append(append(status.EogPlayers, status.LeftPlayers...), status.ReadyPlayers...)
		}
	}
	// Refresh the ballot before each write. A manual honor or another client update may
	// consume votes while automation is preparing the next teammate.
	for {
		if !runner.honorEnabled(strategy) {
			return context.Canceled
		}
		if err := ctx.Err(); err != nil {
			return err
		}
		var current *HonorBallot
		if err := runner.get(ctx, "/lol-honor-v2/v1/ballot/", &current); err != nil {
			return err
		}
		if current == nil || current.GameID != initial.GameID {
			return nil
		}
		excluded := map[string]bool{summoner.Puuid: true}
		for _, player := range current.HonoredPlayers {
			excluded[player.RecipientPuuid] = true
		}
		unacknowledged := 0
		for puuid := range progress.attempted {
			if !excluded[puuid] {
				unacknowledged++
			}
			excluded[puuid] = true
		}
		available := max(current.VotePool.Votes-unacknowledged, 0)
		candidates := SelectHonorCandidates(strategy, current.EligibleAllies, current.EligibleOpponents, lobbyMembers, excluded, available)
		if len(candidates) == 0 {
			break
		}
		puuid := candidates[0]
		// An uncertain network result must never cause the same person to be voted for twice.
		progress.attempted[puuid] = true
		if err := runner.write(ctx, settings.GameflowNamespace, "error-auto-honor", "POST", "/lol-honor/v1/honor", map[string]any{"honorType": "HEART", "recipientPuuid": puuid}); err != nil {
			return err
		}
	}
	if !runner.honorEnabled(strategy) {
		return context.Canceled
	}
	if err := runner.write(ctx, settings.GameflowNamespace, "error-auto-honor", "POST", "/lol-honor/v1/ballot", nil); err != nil {
		return err
	}
	progress.completed = true
	if len(runner.honorGames) > 32 {
		for gameID := range runner.honorGames {
			if gameID != initial.GameID {
				delete(runner.honorGames, gameID)
			}
		}
	}
	return nil
}

func (runner *Runner) honorEnabled(strategy string) bool {
	return runner.settings.Get(settings.GameflowNamespace, "autoHonorEnabled") == true && runner.settings.Get(settings.GameflowNamespace, "autoHonorStrategy") == strategy
}

func validHonorStrategy(strategy string) bool {
	switch strategy {
	case "prefer-lobby-member", "only-lobby-member", "all-member", "all-member-including-opponent", "opt-out":
		return true
	}
	return false
}

// SelectHonorCandidates preserves premade priority, vote limits and teammate-only rules.
func SelectHonorCandidates(strategy string, allies, opponents []EligiblePlayer, lobbyMembers []string, excluded map[string]bool, votes int) []string {
	if votes <= 0 || strategy == "opt-out" || !validHonorStrategy(strategy) {
		return nil
	}
	premadeSet := map[string]bool{}
	for _, puuid := range lobbyMembers {
		premadeSet[puuid] = true
	}
	seen := map[string]bool{}
	var premade, otherAllies, allAllies, otherPlayers []string
	for _, player := range allies {
		if player.BotPlayer || player.Puuid == "" || excluded[player.Puuid] || seen[player.Puuid] {
			continue
		}
		seen[player.Puuid] = true
		allAllies = append(allAllies, player.Puuid)
		if premadeSet[player.Puuid] {
			premade = append(premade, player.Puuid)
		} else {
			otherAllies = append(otherAllies, player.Puuid)
		}
	}
	for _, player := range opponents {
		if player.BotPlayer || player.Puuid == "" || excluded[player.Puuid] || seen[player.Puuid] {
			continue
		}
		seen[player.Puuid] = true
		otherPlayers = append(otherPlayers, player.Puuid)
	}
	var groups [][]string
	switch strategy {
	case "only-lobby-member":
		groups = [][]string{premade}
	case "all-member":
		groups = [][]string{allAllies}
	case "all-member-including-opponent":
		groups = [][]string{premade, otherAllies, otherPlayers}
	default:
		groups = [][]string{premade, otherAllies}
	}
	var result []string
	for _, group := range groups {
		rand.Shuffle(len(group), func(a, b int) { group[a], group[b] = group[b], group[a] })
		count := min(votes-len(result), len(group))
		if count > 0 {
			result = append(result, group[:count]...)
		}
	}
	return result
}

func missingResource(err error) bool {
	var status interface{ StatusCode() int }
	if errors.As(err, &status) {
		return status.StatusCode() == 404
	}
	return err != nil && strings.Contains(err.Error(), "404")
}
