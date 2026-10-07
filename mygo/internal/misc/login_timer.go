package misc

import (
	"context"
	"encoding/json"
	"time"
)

func (service *Service) cancelLoginTimer() {
	service.loginTimerGeneration++
	if service.stopLoginTimer != nil {
		service.stopLoginTimer()
		service.stopLoginTimer = nil
	}
}

func (service *Service) scheduleLoginAutomation() {
	service.cancelLoginTimer()
	generation := service.loginTimerGeneration
	ctx := service.runtimeContext
	release := func() {}
	if client, ok := service.client.(interface {
		RequestScope(context.Context) (context.Context, context.CancelFunc)
	}); ok {
		ctx, release = client.RequestScope(ctx)
	}
	stop := service.after(2*time.Second, func() {
		defer release()
		if ctx.Err() != nil {
			return
		}
		operation, finish := service.begin(ctx)
		defer finish()
		if operation.Err() != nil || generation != service.loginTimerGeneration || service.loginDone {
			return
		}
		var config Config
		if service.store.Decode("auto-misc-main", &config) != nil {
			return
		}
		_ = service.applyLoginAutomation(operation, config)
	})
	service.stopLoginTimer = func() { stop(); release() }
}

// The same cached chat/state notifications consumed by the original reactions
// drive the native backend; no periodic HTTP read is needed to start the timer.
func (service *Service) observeClientState(parent context.Context, view map[string]any) error {
	ctx, finish := service.begin(parent)
	defer finish()
	state, _ := view["state"].(map[string]any)
	if state["connectionState"] != "connected" {
		service.reset()
		return nil
	}
	auth, _ := json.Marshal(state["auth"])
	if service.connectionSignature != string(auth) {
		service.reset()
		service.connectionSignature = string(auth)
	}
	chat, _ := view["chat"].(map[string]any)
	if chat["me"] == nil {
		service.reset()
		return nil
	}
	var me chatMe
	if err := decode(chat["me"], &me); err != nil {
		return err
	}
	var config Config
	if err := service.store.Decode("auto-misc-main", &config); err != nil {
		return err
	}
	return service.applyPresence(ctx, me, config)
}
