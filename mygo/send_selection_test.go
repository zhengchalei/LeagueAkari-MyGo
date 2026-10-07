package main

import (
	"context"
	"errors"
	"fmt"
	"net/http"
	"net/http/httptest"
	"path/filepath"
	"reflect"
	"strings"
	"testing"
	"time"

	"github.com/zhengchalei/LeagueAkari-MyGo/mygo/internal/bridge"
	"github.com/zhengchalei/LeagueAkari-MyGo/mygo/internal/client"
	"github.com/zhengchalei/LeagueAkari-MyGo/mygo/internal/game"
	"github.com/zhengchalei/LeagueAkari-MyGo/mygo/internal/settings"
)

func selectionDesktop() (*Desktop, *[]bridge.Event) {
	d := &Desktop{static: staticStates()}
	events := []bridge.Event{}
	d.eventSink = func(event bridge.Event) { events = append(events, event) }
	return d, &events
}

type presetHTTPBackend struct{ lc *client.Client }

func (b presetHTTPBackend) State() map[string]any { return b.lc.State() }
func (b presetHTTPBackend) CurrentServer() string { return b.lc.CurrentServer() }
func (b presetHTTPBackend) RequestScope(ctx context.Context) (context.Context, context.CancelFunc) {
	return b.lc.RequestScope(ctx)
}
func (b presetHTTPBackend) JSON(ctx context.Context, method, path string, body any) (any, error) {
	return b.lc.JSON(ctx, method, path, body)
}
func (b presetHTTPBackend) SGPJSON(ctx context.Context, server, token, method, path string, body any) (any, error) {
	return b.lc.JSON(ctx, method, path, body)
}

func TestPresetLateTimelineCannotReturnAfterDraftOrPIDChanges(t *testing.T) {
	for _, action := range []string{"draft", "pid"} {
		t.Run(action, func(t *testing.T) {
			started, release := make(chan struct{}, 1), make(chan struct{})
			server := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
				if strings.HasSuffix(r.URL.Path, "/SUMMARY") {
					_, _ = fmt.Fprint(w, `{"games":[{"json":{"gameId":101,"gameType":"MATCHED_GAME","mapId":11,"gameMode":"CLASSIC","queueId":420,"gameDuration":1200,"participants":[{"puuid":"p1","participantId":1,"teamId":100,"championId":64,"teamPosition":"JUNGLE","win":true,"kills":3,"deaths":1,"assists":4}]}}]}`)
					return
				}
				if strings.HasSuffix(r.URL.Path, "/DETAILS") {
					started <- struct{}{}
					<-release
					_, _ = fmt.Fprint(w, `{"json":{"frames":[]}}`)
					return
				}
				_, _ = fmt.Fprint(w, `{}`)
			}))
			defer server.Close()
			lc := client.NewWithOptions(nil, client.Options{HTTPClient: server.Client()})
			lc.SetAuth(&client.Auth{PID: 1, BaseURL: server.URL, Region: "TENCENT", PlatformID: "HN1"})
			store, err := settings.New(filepath.Join(t.TempDir(), "settings.json"))
			if err != nil {
				t.Fatal(err)
			}
			_ = store.Set("ongoing-game-main", "gameDetailsLoadCount", 0)
			d, _ := selectionDesktop()
			d.store = store
			d.client = lc
			d.game = game.New(presetHTTPBackend{lc}, d.emit)
			d.game.SetSettings(store)
			if err = d.game.SetDraft(object{"queueId": 420, "gameModeKind": "normal", "teams": object{"TEAM-100": []any{"p1"}}, "championSelections": object{"p1": 64}, "positions": object{"p1": object{"selected": "jungle"}}}); err != nil {
				t.Fatal(err)
			}
			if err = d.game.ReloadPlayerWithOptions(context.Background(), "p1", object{"includes": []any{"matchHistory"}}); err != nil {
				t.Fatal(err)
			}
			done := make(chan error, 1)
			go func() {
				_, err := d.sendCall(context.Background(), "generateJunglePresetLines", []any{"all"})
				done <- err
			}()
			select {
			case <-started:
			case <-time.After(2 * time.Second):
				close(release)
				<-done
				t.Fatal("actual timeline request not reached")
			}
			if action == "draft" {
				_ = d.game.SetDraft(object{"queueId": 450, "gameModeKind": "normal", "teams": object{"TEAM-100": []any{"p2"}}})
			} else {
				lc.SetAuth(&client.Auth{PID: 2, BaseURL: server.URL, Region: "TENCENT", PlatformID: "HN1"})
			}
			close(release)
			select {
			case err := <-done:
				if !errors.Is(err, context.Canceled) {
					t.Fatal("old preset response returned after roster/client change", err)
				}
			case <-time.After(time.Second):
				t.Fatal("stale generation did not finish")
			}
			if action == "draft" {
				checkSelection(t, d, "ratingPuuids", []any{"p2"})
				checkSelection(t, d, "junglePuuids", []any{})
			}
		})
	}
}
func applySelectionState(d *Desktop, state object) {
	d.presetSelections.mu.Lock()
	defer d.presetSelections.mu.Unlock()
	d.syncPresetSelectionsLocked(state)
}
func checkSelection(t *testing.T, d *Desktop, key string, want []any) {
	t.Helper()
	d.mu.RLock()
	got := d.static["in-game-send-main:state"][key]
	d.mu.RUnlock()
	if !reflect.DeepEqual(got, want) {
		t.Fatalf("%s: got %v want %v", key, got, want)
	}
}

func TestPresetSelectionDefaultsAndStructuralChangesPreserveManualChoice(t *testing.T) {
	d, events := selectionDesktop()
	state := object{"teams": object{"TEAM-100": []any{"p1", "p2"}, "TEAM-200": []any{"p3"}}, "positionAssignments": object{"p1": object{"position": "jungle"}, "p2": object{"position": "TOP"}}, "additional": object{"spells": object{"p3": object{"spell1Id": 4, "spell2Id": 11}}}, "mergedPremadeTeamMap": object{"p1": 1, "p2": 1, "p3": 0}}
	applySelectionState(d, state)
	checkSelection(t, d, "ratingPuuids", []any{"p1", "p2", "p3"})
	checkSelection(t, d, "junglePuuids", []any{"p1", "p3"})
	checkSelection(t, d, "premadeIndices", []any{0.0, 1.0})
	for _, event := range *events {
		if event.Namespace != "mobx-utils-main" || event.Name != "update-state-prop/in-game-send-main:state" || len(event.Args) != 3 {
			t.Fatal("renderer state protocol changed", event)
		}
	}
	d.publishPresetSelection("ratingPuuids", []any{"p2"})
	d.publishPresetSelection("junglePuuids", []any{})
	d.publishPresetSelection("premadeIndices", []any{})
	before := len(*events)
	next := client.Clone(state).(object)
	next["analysis"] = object{"players": object{"p1": object{"summary": object{"kda": 3}}}}
	next["championSelections"] = object{"p1": 64}
	applySelectionState(d, next)
	checkSelection(t, d, "ratingPuuids", []any{"p2"})
	checkSelection(t, d, "junglePuuids", []any{})
	checkSelection(t, d, "premadeIndices", []any{})
	if len(*events) != before {
		t.Fatal("unrelated data refresh reset user choices")
	}
	asObject(next["positionAssignments"])["p2"] = object{"position": "JUNGLE"}
	applySelectionState(d, next)
	checkSelection(t, d, "ratingPuuids", []any{"p1", "p2", "p3"})
	checkSelection(t, d, "junglePuuids", []any{"p1", "p2", "p3"})
	checkSelection(t, d, "premadeIndices", []any{})
	d.publishPresetSelection("ratingPuuids", []any{})
	asObject(next["additional"])["spells"] = object{}
	applySelectionState(d, next)
	checkSelection(t, d, "ratingPuuids", []any{"p1", "p2", "p3"})
	checkSelection(t, d, "junglePuuids", []any{"p1", "p2"})
	d.publishPresetSelection("ratingPuuids", []any{"p2"})
	next["mergedPremadeTeamMap"] = object{"p1": 2, "p2": 2}
	applySelectionState(d, next)
	checkSelection(t, d, "premadeIndices", []any{2.0})
	checkSelection(t, d, "ratingPuuids", []any{"p2"})
	next["teams"] = object{}
	next["mergedPremadeTeamMap"] = object{}
	applySelectionState(d, next)
	for _, key := range []string{"ratingPuuids", "junglePuuids", "premadeIndices"} {
		checkSelection(t, d, key, []any{})
	}
}

func TestPresetSelectionCallsFilterCurrentRosterAndGroups(t *testing.T) {
	d, _ := selectionDesktop()
	d.game = game.New(nil, d.emit)
	if err := d.game.SetDraft(object{"queueId": 420, "gameModeKind": "normal", "teams": object{"TEAM-100": []any{"p1", "p2"}}, "positions": object{"p1": object{"selected": "jungle"}}}); err != nil {
		t.Fatal(err)
	}
	checkSelection(t, d, "ratingPuuids", []any{"p1", "p2"})
	checkSelection(t, d, "junglePuuids", []any{"p1"})
	if _, err := d.sendCall(context.Background(), "setRatingPuuids", []any{[]any{"foreign", "p2", "p2", 123}}); err != nil {
		t.Fatal(err)
	}
	checkSelection(t, d, "ratingPuuids", []any{"p2"})
	if _, err := d.sendCall(context.Background(), "setJunglePuuids", []any{[]any{"p2", "foreign", "p2"}}); err != nil {
		t.Fatal(err)
	}
	checkSelection(t, d, "junglePuuids", []any{"p2"})
	if _, err := d.sendCall(context.Background(), "setPremadeIndices", []any{[]any{1.5, 1.0, "1", -1}}); err != nil {
		t.Fatal(err)
	}
	checkSelection(t, d, "premadeIndices", []any{})
	_, _ = d.sendCall(context.Background(), "clearPresetSelections", nil)
	for _, key := range []string{"ratingPuuids", "junglePuuids", "premadeIndices"} {
		checkSelection(t, d, key, []any{})
	}
	_ = d.state("in-game-send-main", "state")
	checkSelection(t, d, "ratingPuuids", []any{})
	if err := d.game.SetDraft(object{"queueId": 420, "gameModeKind": "normal", "teams": object{"TEAM-100": []any{"fresh"}}}); err != nil {
		t.Fatal(err)
	}
	checkSelection(t, d, "ratingPuuids", []any{"fresh"})
	checkSelection(t, d, "junglePuuids", []any{})
}

func TestPresetGroupIndexFiltersIntegerMembershipWithoutInventingPositiveRule(t *testing.T) {
	state := object{"mergedPremadeTeamMap": object{"p1": 0, "p2": 2, "p3": -1}}
	got := filterPresetSelection(state, "premadeIndices", []any{2.0, 2, 0, -1, 1.5, 3, "2"})
	if !reflect.DeepEqual(got, []any{2.0, 0.0, -1.0}) {
		t.Fatal("original integer/set membership filter drifted", got)
	}
}
