package main

import (
	"bytes"
	"context"
	"fmt"
	"image"
	"log"
	"net/http"
	"net/url"
	"strings"
	"sync"
	"sync/atomic"
	"time"

	"github.com/egoist/mygo"
	"github.com/egoist/mygo/ui"
	"github.com/zhengchalei/LeagueAkari-MyGo/mygo/internal/client"
	nativemini "github.com/zhengchalei/LeagueAkari-MyGo/mygo/native_mini"
	"golang.org/x/image/draw"
)

type miniAsset struct {
	bitmap *ui.Bitmap
	retry  time.Time
}

// A hidden Mini does no polling or painting. Only local LCU asset requests
// and selection writes run off the UI thread; the window hosts no page.
type nativeMiniRuntime struct {
	desktop *Desktop
	model   *nativemini.Model
	view    *nativemini.View
	window  *mygo.Window
	ctx     context.Context
	cancel  context.CancelFunc
	visible atomic.Bool
	wakeCh  chan struct{}
	assetCh chan string
	mu      sync.Mutex
	assets  map[string]miniAsset
	control nativemini.Controls
}

func (d *Desktop) newNativeMini() *nativeMiniRuntime {
	parent := d.ctx
	if parent == nil {
		parent = context.Background()
	}
	ctx, cancel := context.WithCancel(parent)
	m := &nativeMiniRuntime{desktop: d, ctx: ctx, cancel: cancel, wakeCh: make(chan struct{}, 1), assetCh: make(chan string, 128), assets: map[string]miniAsset{}}
	m.model = nativemini.New(d.client.JSON)
	m.view = nativemini.NewView(m.model.Snapshot, m.asset, m.action)
	m.view.Controls = func() nativemini.Controls {
		m.mu.Lock()
		defer m.mu.Unlock()
		return m.control
	}
	return m
}

func (m *nativeMiniRuntime) attach(win *mygo.Window) {
	m.window = win
	m.model.OnChange = win.Invalidate
	go m.run()
	for range 2 {
		go m.loadAssets()
	}
}

func (m *nativeMiniRuntime) wake() {
	select {
	case m.wakeCh <- struct{}{}:
	default:
	}
}

func (m *nativeMiniRuntime) run() {
	ticker := time.NewTicker(time.Second)
	defer ticker.Stop()
	for {
		select {
		case <-m.ctx.Done():
			return
		case <-m.wakeCh:
		case <-ticker.C:
		}
		if m.visible.Load() {
			m.refresh()
		}
	}
}

func (m *nativeMiniRuntime) refresh() {
	d := m.desktop
	view := d.client.State()
	phase := client.String(client.Map(view["gameflow"])["phase"])
	session := client.Map(client.Map(view["champSelect"])["session"])
	auto := d.automation.State()
	selectState := client.Map(auto["auto-select-main"])
	flowState := client.Map(auto["auto-gameflow-main"])
	control := nativemini.Controls{
		Theme: client.String(d.settingValue("app-common-main", "theme")), Locale: client.String(d.settingValue("app-common-main", "locale")),
		Pinned:                   d.settingValue("window-manager-main/aux-window", "pinned") == true,
		CanDodge:                 phase == "ChampSelect" && session["isSpectating"] != true && client.Map(client.Map(client.Map(view["gameflow"])["session"])["gameData"])["isCustomGame"] != true,
		TemporarilyDisabled:      selectState["temporarilyDisabled"] == true,
		CanAccept:                phase == "ReadyCheck" && client.Map(client.Map(view["matchmaking"])["readyCheck"])["playerResponse"] == "None",
		CanCancel:                phase == "Matchmaking",
		CanCancelAutoAccept:      flowState["willAccept"] == true,
		CanCancelAutoMatchmaking: flowState["willSearchMatch"] == true,
	}
	switch control.Theme {
	case "sakura", "butter", "mint":
		control.Theme = "light"
	case "graphite", "cyber", "aurora":
		control.Theme = "dark"
	}
	for _, plan := range []struct{ key, label string }{{"delayedPick", "自动选择"}, {"delayedBan", "自动禁用"}, {"delayedBenchSwap", "自动交换"}, {"delayedChampionSwap", "自动接受交换"}} {
		value := client.Map(selectState[plan.key])
		if len(value) > 0 {
			seconds := max(0, float64(client.Number(value["finishAt"])-time.Now().UnixMilli())/1000)
			control.Plans = append(control.Plans, fmt.Sprintf("%s · %.1f 秒", plan.label, seconds))
		}
	}
	if control.CanCancelAutoAccept {
		control.Plans = append(control.Plans, fmt.Sprintf("自动接受对局 · %.1f 秒", max(0, float64(client.Number(flowState["willAcceptAt"])-time.Now().UnixMilli())/1000)))
	}
	m.mu.Lock()
	m.control = control
	m.mu.Unlock()
	m.model.Refresh(m.ctx, nativemini.Inputs{Client: view, Kiwi: d.state("extra-assets-main", "kiwi"), Fandom: d.state("extra-assets-main", "fandom"), OPGG: d.state("extra-assets-main", "opgg"), ShowSkins: d.settingValue("window-manager-main/aux-window", "showSkinSelector") != false})
}

func (m *nativeMiniRuntime) action(action nativemini.Action) {
	go func() {
		ctx, cancel := context.WithTimeout(m.ctx, 15*time.Second)
		defer cancel()
		d := m.desktop
		var err error
		switch action.Kind {
		case "champion":
			err = m.model.ChooseChampion(ctx, action.ID, true)
		case "skin":
			err = m.model.ChooseSkin(ctx, action.ID)
		case "reroll", "reroll-grab-back":
			err = m.model.Reroll(ctx, action.Kind == "reroll-grab-back")
		case "pin":
			err = d.store.Set("window-manager-main/aux-window", "pinned", action.Value)
		case "disable-auto":
			d.automation.SetTemporarilyDisabled(action.Value)
		case "accept", "decline":
			if client.String(client.Map(d.client.State()["gameflow"])["phase"]) == "ReadyCheck" {
				_, err = d.client.JSON(ctx, http.MethodPost, "/lol-matchmaking/v1/ready-check/"+action.Kind, nil)
			}
		case "cancel-queue":
			if client.String(client.Map(d.client.State()["gameflow"])["phase"]) == "Matchmaking" {
				_, err = d.client.JSON(ctx, http.MethodDelete, "/lol-lobby/v2/lobby/matchmaking/search", nil)
			}
		case "cancel-auto-accept":
			d.automation.CancelAutoAccept()
		case "cancel-auto-matchmaking":
			d.automation.CancelAutoMatchmaking()
		case "dodge":
			response, dialogErr := mygo.Dialog.Message(mygo.MessageOptions{Parent: m.window, Type: mygo.MessageWarning, Title: "退出英雄选择", Message: "确定退出当前英雄选择？可能产生等待惩罚。", Buttons: []string{"取消", "退出"}, DefaultButton: 0, CancelButton: 0})
			if dialogErr != nil {
				err = dialogErr
			} else if response.Button == 1 && client.String(client.Map(d.client.State()["gameflow"])["phase"]) == "ChampSelect" {
				params := url.Values{"destination": {"lcdsServiceProxy"}, "method": {"call"}, "args": {`["", "teambuilder-draft", "quitV2", ""]`}}
				_, err = d.client.JSON(ctx, http.MethodPost, "/lol-login/v1/session/invoke?"+params.Encode(), object{"data": []any{"", "teambuilder-draft", "quitV2", ""}})
			}
		}
		if err != nil {
			log.Printf("Native Mini %s: %v", action.Kind, err)
			_, _ = mygo.Dialog.Message(mygo.MessageOptions{Parent: m.window, Type: mygo.MessageError, Title: "操作未完成", Message: err.Error()})
		}
		m.wake()
	}()
}

func (m *nativeMiniRuntime) asset(path string) *ui.Bitmap {
	if !strings.HasPrefix(path, "/lol-game-data/assets/") {
		return nil
	}
	m.mu.Lock()
	defer m.mu.Unlock()
	if value, exists := m.assets[path]; exists {
		if value.bitmap != nil || value.retry.IsZero() || time.Now().Before(value.retry) {
			return value.bitmap
		}
	}
	if len(m.assets) >= 128 {
		for key, value := range m.assets {
			if !value.retry.IsZero() {
				delete(m.assets, key)
				break
			}
		}
		if len(m.assets) >= 128 {
			return nil
		}
	}
	select {
	case m.assetCh <- path:
		m.assets[path] = miniAsset{}
	default:
	}
	return nil
}

func (m *nativeMiniRuntime) loadAssets() {
	for {
		select {
		case <-m.ctx.Done():
			return
		case path := <-m.assetCh:
			ctx, cancel := context.WithTimeout(m.ctx, 8*time.Second)
			data, err := m.desktop.client.JSONRaw(ctx, http.MethodGet, path, nil)
			cancel()
			var bitmap *ui.Bitmap
			if err == nil && len(data) <= 8<<20 {
				if config, _, e := image.DecodeConfig(bytes.NewReader(data)); e == nil && config.Width*config.Height <= 32<<20 {
					if img, _, e := image.Decode(bytes.NewReader(data)); e == nil {
						bounds := img.Bounds()
						w, h := bounds.Dx(), bounds.Dy()
						if max(w, h) > 160 {
							scale := float64(160) / float64(max(w, h))
							thumb := image.NewRGBA(image.Rect(0, 0, max(1, int(float64(w)*scale)), max(1, int(float64(h)*scale))))
							draw.ApproxBiLinear.Scale(thumb, thumb.Bounds(), img, bounds, draw.Src, nil)
							img = thumb
						}
						bitmap = ui.NewBitmap(img)
					}
				}
			}
			m.mu.Lock()
			m.assets[path] = miniAsset{bitmap: bitmap, retry: time.Now().Add(15 * time.Second)}
			m.mu.Unlock()
			m.window.Invalidate()
		}
	}
}
