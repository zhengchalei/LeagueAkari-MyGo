package misc

import (
	"context"
	"encoding/json"
	"errors"
	"fmt"
	"net/url"
	"strings"
	"sync"
	"time"

	"github.com/zhengchalei/LeagueAkari-MyGo/mygo/internal/bridge"
	"github.com/zhengchalei/LeagueAkari-MyGo/mygo/internal/settings"
)

type JSONClient interface {
	JSON(context.Context, string, string, any) (any, error)
}

type RankedStatus struct {
	Queue    string `json:"queue"`
	Tier     string `json:"tier"`
	Division string `json:"division"`
}
type Config struct {
	AutoReplyEnabled            bool         `json:"autoReplyEnabled"`
	AutoReplyEnableOnAway       bool         `json:"autoReplyEnableOnAway"`
	AutoReplyText               string       `json:"autoReplyText"`
	LockOfflineStatus           bool         `json:"lockOfflineStatus"`
	AutoSetStatusMessageEnabled bool         `json:"autoSetStatusMessageEnabled"`
	StatusMessage               string       `json:"statusMessage"`
	AutoSetRankedStatusEnabled  bool         `json:"autoSetRankedStatusEnabled"`
	RankedStatus                RankedStatus `json:"rankedStatus"`
}

type chatMe struct {
	Availability string `json:"availability"`
	Puuid        string `json:"puuid"`
	SummonerID   int64  `json:"summonerId"`
}
type chatMessage struct {
	ID             string `json:"id"`
	Type           string `json:"type"`
	FromSummonerID int64  `json:"fromSummonerId"`
	IsHistorical   bool   `json:"isHistorical"`
	Timestamp      string `json:"timestamp"`
	Body           string `json:"body"`
}

// Service keeps login actions, presence locking and replies under their original
// switches. Construction neither connects to nor changes the League client.
type Service struct {
	client                   JSONClient
	store                    *settings.Store
	emit                     bridge.Emitter
	now                      func() time.Time
	operation                sync.Mutex
	mu                       sync.Mutex
	active                   context.CancelFunc
	unsubscribe              func()
	previousAvailability     string
	meSignature              string
	settledAt                time.Time
	loginDone                bool
	seenMessages             map[string]bool
	messageOrder             []string
	initializedConversations map[string]bool
}

func New(client JSONClient, store *settings.Store, emit bridge.Emitter) *Service {
	service := &Service{client: client, store: store, emit: emit, now: time.Now, seenMessages: map[string]bool{}, initializedConversations: map[string]bool{}}
	service.unsubscribe = store.OnChange(func(namespace, key string) {
		if namespace == settings.MiscNamespace {
			service.mu.Lock()
			if service.active != nil {
				service.active()
			}
			service.mu.Unlock()
		}
	})
	return service
}

func (service *Service) Close() {
	service.mu.Lock()
	if service.active != nil {
		service.active()
	}
	service.mu.Unlock()
	if service.unsubscribe != nil {
		service.unsubscribe()
	}
}
func (service *Service) Run(ctx context.Context) {
	ticker := time.NewTicker(time.Second)
	defer ticker.Stop()
	for {
		if ctx.Err() != nil {
			return
		}
		_ = service.Tick(ctx)
		select {
		case <-ctx.Done():
			return
		case <-ticker.C:
		}
	}
}
func (service *Service) State() map[string]any {
	return map[string]any{settings.MiscNamespace: map[string]any{}}
}

func (service *Service) begin(parent context.Context) (context.Context, func()) {
	service.operation.Lock()
	ctx, cancel := context.WithCancel(parent)
	service.mu.Lock()
	service.active = cancel
	service.mu.Unlock()
	return ctx, func() {
		cancel()
		service.mu.Lock()
		service.active = nil
		service.mu.Unlock()
		service.operation.Unlock()
	}
}

func (service *Service) Tick(parent context.Context) error {
	ctx, finish := service.begin(parent)
	defer finish()
	var config Config
	if err := service.store.Decode(settings.MiscNamespace, &config); err != nil {
		return err
	}
	if !config.AutoReplyEnabled && !config.LockOfflineStatus && !config.AutoSetStatusMessageEnabled && !config.AutoSetRankedStatusEnabled {
		return nil
	}
	var me *chatMe
	if err := service.get(ctx, "/lol-chat/v1/me", &me); err != nil {
		service.reset()
		return err
	}
	if me == nil {
		service.reset()
		return nil
	}
	if err := service.applyPresence(ctx, *me, config); err != nil {
		return err
	}
	if config.AutoReplyEnabled && config.AutoReplyText != "" {
		return service.pollMessages(ctx, *me, config)
	}
	return nil
}

func (service *Service) reset() {
	service.previousAvailability = ""
	service.meSignature = ""
	service.loginDone = false
	service.settledAt = time.Time{}
	service.initializedConversations = map[string]bool{}
	service.seenMessages = map[string]bool{}
	service.messageOrder = nil
}

func (service *Service) applyPresence(ctx context.Context, me chatMe, config Config) error {
	previous := service.previousAvailability
	service.previousAvailability = me.Availability
	if config.LockOfflineStatus && previous == "offline" && (me.Availability == "away" || me.Availability == "chat") {
		if err := service.write(ctx, "PUT", "/lol-chat/v1/me", map[string]any{"availability": "offline"}); err != nil {
			return err
		}
	}
	data, _ := json.Marshal(me)
	signature := string(data)
	if !service.loginDone && signature != service.meSignature {
		service.meSignature = signature
		service.settledAt = service.now().Add(2 * time.Second)
	}
	if service.loginDone || service.settledAt.IsZero() || service.now().Before(service.settledAt) {
		return nil
	}
	service.loginDone = true
	var failures []error
	if config.AutoSetStatusMessageEnabled {
		if err := service.write(ctx, "PUT", "/lol-chat/v1/me", map[string]any{"statusMessage": config.StatusMessage}); err != nil {
			failures = append(failures, err)
		}
	}
	if config.AutoSetRankedStatusEnabled {
		if err := service.write(ctx, "PUT", "/lol-chat/v1/me", rankedPayload(config.RankedStatus)); err != nil {
			failures = append(failures, err)
		}
	}
	return errors.Join(failures...)
}

func (service *Service) Call(parent context.Context, name string, args []any) (any, error) {
	ctx, finish := service.begin(parent)
	defer finish()
	service.loginDone = true
	var config Config
	if err := service.store.Decode(settings.MiscNamespace, &config); err != nil {
		return nil, err
	}
	switch name {
	case "applyStatusMessage":
		message := config.StatusMessage
		if len(args) > 0 && args[0] != nil {
			var ok bool
			message, ok = args[0].(string)
			if !ok {
				return nil, errors.New("status message must be a string")
			}
		}
		return nil, service.write(ctx, "PUT", "/lol-chat/v1/me", map[string]any{"statusMessage": message})
	case "applyRankedStatus":
		status := config.RankedStatus
		if len(args) > 0 && args[0] != nil {
			if err := decode(args[0], &status); err != nil {
				return nil, err
			}
		}
		return nil, service.write(ctx, "PUT", "/lol-chat/v1/me", rankedPayload(status))
	default:
		return nil, fmt.Errorf("unknown auto-misc call %s", name)
	}
}

func rankedPayload(status RankedStatus) map[string]any {
	lol := map[string]any{"rankedLeagueQueue": status.Queue, "rankedLeagueTier": status.Tier}
	if status.Tier != "MASTER" && status.Tier != "GRANDMASTER" && status.Tier != "CHALLENGER" {
		lol["rankedLeagueDivision"] = status.Division
	}
	return map[string]any{"lol": lol}
}

// HandleLCUEvent accepts the client's decoded WebSocket event directly.
func (service *Service) HandleLCUEvent(parent context.Context, uri, eventType string, data any) error {
	if uri != "/lol-chat/v1/me" && !strings.HasPrefix(uri, "/lol-chat/v1/conversations/") {
		return nil
	}
	ctx, finish := service.begin(parent)
	defer finish()
	var config Config
	if err := service.store.Decode(settings.MiscNamespace, &config); err != nil {
		return err
	}
	if uri == "/lol-chat/v1/me" {
		if eventType == "Delete" || data == nil {
			service.reset()
			return nil
		}
		var me chatMe
		if err := decode(data, &me); err != nil {
			return err
		}
		return service.applyPresence(ctx, me, config)
	}
	parts := strings.Split(strings.TrimPrefix(uri, "/lol-chat/v1/conversations/"), "/")
	if len(parts) != 3 || parts[1] != "messages" || eventType == "Delete" || !config.AutoReplyEnabled || config.AutoReplyText == "" {
		return nil
	}
	var message chatMessage
	if err := decode(data, &message); err != nil {
		return err
	}
	if message.ID == "" {
		message.ID = parts[2]
	}
	var me chatMe
	if err := service.get(ctx, "/lol-chat/v1/me", &me); err != nil {
		return err
	}
	return service.reply(ctx, parts[0], message, me, config)
}

func (service *Service) pollMessages(ctx context.Context, me chatMe, config Config) error {
	var conversations []struct {
		ID   string `json:"id"`
		Type string `json:"type"`
	}
	if err := service.get(ctx, "/lol-chat/v1/conversations", &conversations); err != nil {
		return err
	}
	active := map[string]bool{}
	for _, conversation := range conversations {
		if conversation.Type != "chat" {
			continue
		}
		active[conversation.ID] = true
		var messages []chatMessage
		if err := service.get(ctx, "/lol-chat/v1/conversations/"+url.PathEscape(conversation.ID)+"/messages", &messages); err != nil {
			continue
		}
		initialized := service.initializedConversations[conversation.ID]
		service.initializedConversations[conversation.ID] = true
		for _, message := range messages {
			if !initialized {
				service.remember(conversation.ID + ":" + messageKey(message))
				continue
			}
			if err := service.reply(ctx, conversation.ID, message, me, config); err != nil {
				return err
			}
		}
	}
	for id := range service.initializedConversations {
		if !active[id] {
			delete(service.initializedConversations, id)
		}
	}
	return nil
}

func (service *Service) reply(ctx context.Context, conversation string, message chatMessage, me chatMe, config Config) error {
	if message.Type != "chat" || message.IsHistorical || (config.AutoReplyEnableOnAway && me.Availability != "away") {
		return nil
	}
	var summoner struct {
		SummonerID int64 `json:"summonerId"`
	}
	if err := service.get(ctx, "/lol-summoner/v1/current-summoner", &summoner); err != nil {
		return err
	}
	if summoner.SummonerID == 0 || message.FromSummonerID == summoner.SummonerID {
		return nil
	}
	key := conversation + ":" + messageKey(message)
	if service.seenMessages[key] {
		return nil
	}
	service.remember(key)
	if service.store.Get(settings.MiscNamespace, "autoReplyEnabled") != true {
		return context.Canceled
	}
	err := service.write(ctx, "POST", "/lol-chat/v1/conversations/"+url.PathEscape(conversation)+"/messages", map[string]any{"body": config.AutoReplyText, "fromPid": "", "fromSummonerId": 0, "id": conversation, "isHistorical": false, "timestamp": "", "type": "chat"})
	if err != nil && service.emit != nil {
		service.emit(settings.MiscNamespace, "error-send-failed", map[string]any{"error": map[string]any{"message": err.Error()}})
	}
	return err
}

func messageKey(message chatMessage) string {
	if message.ID != "" {
		return message.ID
	}
	return fmt.Sprintf("%d:%s:%s", message.FromSummonerID, message.Timestamp, message.Body)
}
func (service *Service) remember(key string) {
	if service.seenMessages[key] {
		return
	}
	service.seenMessages[key] = true
	service.messageOrder = append(service.messageOrder, key)
	if len(service.messageOrder) > 1024 {
		delete(service.seenMessages, service.messageOrder[0])
		service.messageOrder = service.messageOrder[1:]
	}
}
func (service *Service) get(ctx context.Context, path string, target any) error {
	value, err := service.client.JSON(ctx, "GET", path, nil)
	if err != nil {
		return err
	}
	return decode(value, target)
}
func (service *Service) write(ctx context.Context, method, path string, body any) error {
	if err := ctx.Err(); err != nil {
		return err
	}
	_, err := service.client.JSON(ctx, method, path, body)
	return err
}
func decode(value, target any) error {
	data, err := json.Marshal(value)
	if err != nil {
		return err
	}
	return json.Unmarshal(data, target)
}

// HandleEvent can consume a generic emitter packet when the host forwards an LCU event.
func (service *Service) HandleEvent(ctx context.Context, namespace, name string, args ...any) error {
	if namespace != "league-client-main" || name != "lcu-event" || len(args) < 3 {
		return nil
	}
	uri, _ := args[0].(string)
	eventType, _ := args[1].(string)
	return service.HandleLCUEvent(ctx, uri, eventType, args[2])
}
