package main

import (
	"context"
	"net/http"
	"net/url"
	"sync"
	"time"
)

// Match the original Mini's five parallel attempts. The selection phase and
// application context bound every worker, including after the host disconnects.
func (d *Desktop) startWinUIDodgeLoop(requestCtx context.Context) error {
	if !d.winUIMiniControls().CanDodge || d.winUIDodgeActive.Load() {
		return nil
	}
	confirmation, err := d.hostCall(requestCtx, "host-ui", "confirm", []any{"退出英雄选择", "确定循环尝试退出当前英雄选择？可能产生等待惩罚。"})
	if err != nil {
		return err
	}
	if confirmation != true {
		return nil
	}
	d.winUIDodgeMu.Lock()
	if !d.winUIDodgeActive.CompareAndSwap(false, true) {
		d.winUIDodgeMu.Unlock()
		return nil
	}
	ctx, cancel := context.WithCancel(d.ctx)
	d.winUIDodgeCancel = cancel
	d.winUIDodgeCount.Store(0)
	d.winUIDodgeMu.Unlock()
	d.emit("winui-backend", "miniChanged")
	go func() {
		defer func() {
			cancel()
			d.winUIDodgeMu.Lock()
			d.winUIDodgeCancel = nil
			d.winUIDodgeActive.Store(false)
			d.winUIDodgeMu.Unlock()
			d.emit("winui-backend", "miniChanged")
		}()
		params := url.Values{"destination": {"lcdsServiceProxy"}, "method": {"call"}, "args": {`["", "teambuilder-draft", "quitV2", ""]`}}
		var workers sync.WaitGroup
		for range 5 {
			workers.Add(1)
			go func() {
				defer workers.Done()
				for ctx.Err() == nil && d.winUIMiniControls().CanDodge {
					attemptCtx, stop := context.WithTimeout(ctx, 15*time.Second)
					_, _ = d.client.JSON(attemptCtx, http.MethodPost, "/lol-login/v1/session/invoke?"+params.Encode(), object{"data": []any{"", "teambuilder-draft", "quitV2", ""}})
					stop()
					d.winUIDodgeCount.Add(1)
					d.emit("winui-backend", "miniChanged")
				}
			}()
		}
		workers.Wait()
	}()
	return nil
}
