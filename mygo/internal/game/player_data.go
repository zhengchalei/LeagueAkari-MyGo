package game

import (
	"context"
	"errors"
	"fmt"
	"net/http"
	"net/url"
	"strconv"

	"github.com/zhengchalei/LeagueAkari-MyGo/mygo/internal/client"
)

var playerReloadScopes = []string{"matchHistory", "summoner", "rankedStats", "savedInfo", "championMastery"}

func reloadScopes(options map[string]any) ([]string, error) {
	includes, hasIncludes := options["includes"]
	excludes, hasExcludes := options["excludes"]
	if hasIncludes && hasExcludes {
		return nil, errors.New("includes and excludes cannot be used together")
	}
	known := map[string]bool{}
	for _, scope := range playerReloadScopes {
		known[scope] = true
	}
	selected := map[string]bool{}
	value := excludes
	if hasIncludes {
		value = includes
	}
	for _, item := range client.List(value) {
		scope := client.String(item)
		if !known[scope] {
			return nil, fmt.Errorf("unsupported player reload scope: %s", scope)
		}
		selected[scope] = true
	}
	scopes := []string{}
	for _, scope := range playerReloadScopes {
		if (hasIncludes && selected[scope]) || (!hasIncludes && !selected[scope]) {
			scopes = append(scopes, scope)
		}
	}
	return scopes, nil
}

func (s *Service) ReloadPlayerWithOptions(ctx context.Context, puuid string, options map[string]any) error {
	ctx, release := s.requestScope(ctx)
	defer release()
	if !validPUUID(puuid) {
		return errors.New("puuid cannot be empty")
	}
	scopes, err := reloadScopes(options)
	if err != nil {
		return err
	}
	for _, scope := range scopes {
		if scope == "matchHistory" {
			config := s.config()
			var finishPrefetch func()
			ctx, finishPrefetch = s.startDetailsPrefetch(ctx, config.detailsCount, config.concurrency)
			defer finishPrefetch()
			break
		}
	}
	err = s.loadScopes(ctx, puuid, scopes, true)
	s.loadAuxiliaryInfo(ctx)
	return err
}

func (s *Service) loadScopes(ctx context.Context, puuid string, scopes []string, force bool) error {
	var firstErr error
	for _, scope := range scopes {
		if ctx.Err() != nil {
			return ctx.Err()
		}
		if scope == "matchHistory" {
			if err := s.loadMatchHistory(ctx, puuid, force); err != nil && firstErr == nil {
				firstErr = err
			}
			continue
		}
		if scope == "savedInfo" {
			s.loadSavedInfo(puuid, force)
			continue
		}
		if err := s.loadMetadata(ctx, puuid, scope, force); err != nil && firstErr == nil {
			firstErr = err
		}
	}
	return firstErr
}

func (s *Service) loadMetadata(ctx context.Context, puuid, scope string, force bool) error {
	s.mu.RLock()
	_, exists := client.Map(s.data[scope])[puuid]
	s.mu.RUnlock()
	if exists && !force {
		return nil
	}
	paths := map[string]string{
		"summoner":        "/lol-summoner/v2/summoners/puuid/" + url.PathEscape(puuid),
		"rankedStats":     "/lol-ranked/v1/ranked-stats/" + url.PathEscape(puuid),
		"championMastery": "/lol-champion-mastery/v1/" + url.PathEscape(puuid) + "/champion-mastery",
	}
	events := map[string]string{"summoner": "summoner-loaded", "rankedStats": "ranked-stats-loaded", "championMastery": "champion-mastery-loaded"}
	s.loading(scope, puuid, "loading")
	value, err := s.backend.JSON(ctx, http.MethodGet, paths[scope], nil)
	if err != nil {
		if ctx.Err() == nil {
			s.loading(scope, puuid, "error")
		}
		return err
	}
	if err := ctx.Err(); err != nil {
		return err
	}
	if scope == "championMastery" {
		indexed := map[string]any{}
		for _, entry := range client.List(value) {
			row := client.Map(entry)
			indexed[strconv.FormatInt(client.Number(row["championId"]), 10)] = row
		}
		value = indexed
	}
	s.put(scope, events[scope], puuid, value)
	s.loading(scope, puuid, "loaded")
	return nil
}
