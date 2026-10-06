package main

import (
	"context"
	_ "embed"
	"encoding/json"
	"log"
	"os"
	"path/filepath"
	"time"

	"github.com/egoist/mygo"
	"github.com/zhengchalei/LeagueAkari-MyGo/mygo/internal/catalog"
)

//go:embed internal/bridge/assets/kiwi-balance.json
var kiwiBalanceSnapshot []byte

func (d *Desktop) loadKiwiBalance(directory string) {
	data, err := os.ReadFile(filepath.Join(directory, "kiwi-balance.json"))
	if err != nil {
		return
	}
	var snapshot catalog.KiwiSnapshot
	if json.Unmarshal(data, &snapshot) != nil || len(snapshot.Balance) == 0 || snapshot.SourceURL != catalog.KiwiSourceURL || snapshot.Version == "" {
		return
	}
	snapshot.Cached = true
	d.publishKiwiBalance(snapshot)
}

func (d *Desktop) publishKiwiBalance(snapshot catalog.KiwiSnapshot) {
	data, _ := json.Marshal(snapshot)
	var state object
	_ = json.Unmarshal(data, &state)
	d.mu.Lock()
	d.static["extra-assets-main:kiwi"] = state
	d.mu.Unlock()
	for key, value := range state {
		d.update("extra-assets-main", "kiwi", key, value)
	}
}

func (d *Desktop) fetchKiwiBalance(ctx context.Context, sourceURL, directory string) error {
	data, _ := json.Marshal(d.state("extra-assets-main", "kiwi"))
	var previous catalog.KiwiSnapshot
	_ = json.Unmarshal(data, &previous)
	ctx, cancel := context.WithTimeout(ctx, 3*time.Minute)
	defer cancel()
	httpClient := d.externalHTTP()
	defer httpClient.CloseIdleConnections()
	snapshot, err := catalog.FetchKiwiBalance(ctx, httpClient, sourceURL, previous)
	if err != nil {
		d.setStatic("extra-assets-main:kiwi", "cached", true)
		d.update("extra-assets-main", "kiwi", "cached", true)
		return err
	}
	d.publishKiwiBalance(snapshot)
	if directory != "" {
		data, _ = json.Marshal(snapshot)
		target := filepath.Join(directory, "kiwi-balance.json")
		if err := os.WriteFile(target+".tmp", data, 0600); err == nil {
			if err := os.Rename(target+".tmp", target); err != nil {
				log.Printf("RESG cache could not be saved: %v", err)
			}
		} else {
			log.Printf("RESG cache could not be saved: %v", err)
		}
	}
	return nil
}

func (d *Desktop) refreshKiwiBalance(ctx context.Context) {
	directory, _ := mygo.App.Path(mygo.PathUserData)
	for ctx.Err() == nil {
		if err := d.fetchKiwiBalance(ctx, catalog.KiwiSourceURL, directory); err != nil && ctx.Err() == nil {
			log.Printf("RESG balance refresh unavailable; retaining cached data: %v", err)
		}
		timer := time.NewTimer(30 * time.Minute)
		select {
		case <-ctx.Done():
			timer.Stop()
			return
		case <-timer.C:
		}
	}
}
