package client

import (
	"context"
	"net/http"
	"net/url"
	"reflect"
	"strconv"
	"strings"
	"sync"
)

var chatRoomTypes = []string{"championSelect", "postGame", "customGame"}

func chatRoomType(conversation any) string {
	row := Map(conversation)
	id, kind := String(row["id"]), String(row["type"])
	if id == "" || (kind == "championSelect" && !strings.Contains(id, "lol-champ-select")) {
		return ""
	}
	for _, supported := range chatRoomTypes {
		if kind == supported {
			return kind
		}
	}
	return ""
}

// The renderer's chat store is shallow reactive, so room updates replace the
// complete conversations/participants object instead of mutating its children.
func (c *Client) updateChatRooms(update func(conversations, participants map[string]any)) {
	c.mu.Lock()
	chat := Map(c.state["chat"])
	conversations := Map(Clone(chat["conversations"]))
	participants := Map(Clone(chat["participants"]))
	update(conversations, participants)
	changedConversations := !reflect.DeepEqual(chat["conversations"], conversations)
	changedParticipants := !reflect.DeepEqual(chat["participants"], participants)
	chat["conversations"], chat["participants"] = conversations, participants
	c.mu.Unlock()
	if c.emit != nil {
		if changedConversations {
			c.emit("mobx-utils-main", "update-state-prop/league-client-main:chat", "conversations", conversations, map[string]any{"action": "update", "raw": true})
		}
		if changedParticipants {
			c.emit("mobx-utils-main", "update-state-prop/league-client-main:chat", "participants", participants, map[string]any{"action": "update", "raw": true})
		}
	}
}

func (c *Client) replaceChatConversations(value any) {
	rooms := map[string]any{}
	for _, conversation := range List(value) {
		if kind := chatRoomType(conversation); kind != "" {
			rooms[kind] = conversation
		}
	}
	c.updateChatRooms(func(conversations, participants map[string]any) {
		for _, kind := range chatRoomTypes {
			conversation := rooms[kind]
			if conversation == nil {
				participants[kind] = nil
			} else if String(Map(conversations[kind])["id"]) != String(Map(conversation)["id"]) {
				participants[kind] = []any{}
			}
			conversations[kind] = conversation
		}
	})
}

func (c *Client) setChatConversation(value any) {
	kind := chatRoomType(value)
	if kind == "" {
		return
	}
	c.updateChatRooms(func(conversations, participants map[string]any) {
		if String(Map(conversations[kind])["id"]) != String(Map(value)["id"]) {
			participants[kind] = []any{}
		}
		conversations[kind] = value
	})
}

func (c *Client) deleteChatConversation(id string) {
	c.updateChatRooms(func(conversations, participants map[string]any) {
		for _, kind := range chatRoomTypes {
			if String(Map(conversations[kind])["id"]) == id {
				conversations[kind], participants[kind] = nil, nil
			}
		}
	})
}

func participantIDs(value any) []any {
	ids := []any{}
	seen := map[int64]bool{}
	for _, participant := range List(value) {
		id := Number(Map(participant)["summonerId"])
		if id > 0 && !seen[id] {
			ids = append(ids, id)
			seen[id] = true
		}
	}
	return ids
}

func (c *Client) setChatParticipants(conversationID string, value any) {
	c.updateChatRooms(func(conversations, participants map[string]any) {
		for _, kind := range chatRoomTypes {
			if String(Map(conversations[kind])["id"]) == conversationID {
				participants[kind] = participantIDs(value)
			}
		}
	})
}

func (c *Client) changeChatParticipant(conversationID string, id int64, remove bool) {
	if id <= 0 {
		return
	}
	c.updateChatRooms(func(conversations, participants map[string]any) {
		for _, kind := range chatRoomTypes {
			if String(Map(conversations[kind])["id"]) != conversationID {
				continue
			}
			ids := []any{}
			found := false
			for _, existing := range List(participants[kind]) {
				if Number(existing) == id {
					found = true
					if remove {
						continue
					}
				}
				ids = append(ids, existing)
			}
			if !remove && !found {
				ids = append(ids, id)
			}
			participants[kind] = ids
		}
	})
}

func (c *Client) syncChat(ctx context.Context) {
	value, err := c.JSON(ctx, http.MethodGet, "/lol-chat/v1/conversations", nil)
	if err != nil {
		return // Riot chat can become ready after the local client connects.
	}
	c.replaceChatConversations(value)
	var wg sync.WaitGroup
	for _, conversation := range List(value) {
		if chatRoomType(conversation) == "" {
			continue
		}
		id := String(Map(conversation)["id"])
		wg.Add(1)
		go func() {
			defer wg.Done()
			if participants, err := c.JSON(ctx, http.MethodGet, "/lol-chat/v1/conversations/"+url.PathEscape(id)+"/participants", nil); err == nil {
				c.setChatParticipants(id, participants)
			}
		}()
	}
	wg.Wait()
}

func (c *Client) dispatchChatEvent(uri, eventType string, rawData any) {
	if uri == "/lol-chat/v1/conversations" {
		if eventType == "Delete" {
			rawData = nil
		}
		c.replaceChatConversations(rawData)
		return
	}
	if !strings.HasPrefix(uri, "/lol-chat/v1/conversations/") {
		return
	}
	parts := strings.Split(strings.TrimPrefix(uri, "/lol-chat/v1/conversations/"), "/")
	id, err := url.PathUnescape(parts[0])
	if err != nil || id == "" {
		return
	}
	if len(parts) == 1 {
		if eventType == "Delete" {
			c.deleteChatConversation(id)
		} else {
			c.setChatConversation(rawData)
		}
		return
	}
	if parts[1] == "participants" {
		if len(parts) == 2 {
			if eventType == "Delete" {
				rawData = nil
			}
			c.setChatParticipants(id, rawData)
		} else if len(parts) == 3 {
			participantID := Number(Map(rawData)["summonerId"])
			if participantID == 0 {
				participantID, _ = strconv.ParseInt(parts[2], 10, 64)
			}
			c.changeChatParticipant(id, participantID, eventType == "Delete")
		}
	} else if parts[1] == "messages" && len(parts) == 3 && eventType != "Delete" {
		message := Map(rawData)
		if String(message["type"]) == "system" && String(message["body"]) == "joined_room" {
			c.changeChatParticipant(id, Number(message["fromSummonerId"]), false)
		}
	}
}
