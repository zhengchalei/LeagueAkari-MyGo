package main

import (
	"context"
	"encoding/json"
	"fmt"
	"io"
	"log"
	"net/http"
	"strconv"
	"time"
)

func (d *Desktop) refreshBalance(ctx context.Context) {
	for {
		if err := d.fetchBalance(ctx); err != nil && ctx.Err() == nil {
			log.Print("OP.GG balance refresh unavailable; retaining cached data")
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

func (d *Desktop) fetchBalance(ctx context.Context) error {
	request, err := http.NewRequestWithContext(ctx, http.MethodGet, "https://lol-api-champion.op.gg/api/contents/aram-balance", nil)
	if err != nil {
		return err
	}
	response, err := (&http.Client{Timeout: 12 * time.Second}).Do(request)
	if err != nil {
		return err
	}
	defer response.Body.Close()
	if response.StatusCode != 200 {
		return fmt.Errorf("balance status %d", response.StatusCode)
	}
	var result struct {
		Data []object `json:"data"`
	}
	if err := json.NewDecoder(io.LimitReader(response.Body, 2*1024*1024)).Decode(&result); err != nil {
		return err
	}
	if len(result.Data) == 0 {
		return fmt.Errorf("empty balance response")
	}
	balance := object{}
	for _, champion := range result.Data {
		id, ok := champion["champion_id"].(float64)
		if !ok {
			return fmt.Errorf("invalid champion balance")
		}
		balance[strconv.Itoa(int(id))] = champion
	}
	state := object{"balance": balance, "lastUpdate": time.Now().UnixMilli(), "cached": false}
	d.mu.Lock()
	d.static["extra-assets-main:opgg"] = state
	d.mu.Unlock()
	for key, value := range state {
		d.update("extra-assets-main", "opgg", key, value)
	}
	return nil
}
