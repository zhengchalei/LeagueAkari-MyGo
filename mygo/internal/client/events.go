package client

import (
	"context"
	"crypto/rand"
	"crypto/sha1"
	"encoding/base64"
	"encoding/binary"
	"encoding/json"
	"errors"
	"fmt"
	"io"
	"net/http"
	"strconv"
	"strings"
	"time"
)

func (c *Client) Subscribe(uri string) string {
	if !strings.HasPrefix(uri, "/") || credentialEndpoint(uri) {
		return ""
	}
	c.mu.Lock()
	defer c.mu.Unlock()
	c.subscriptionID++
	id := "__go" + strconv.FormatUint(c.subscriptionID, 10)
	c.subscriptions[id] = uri
	return id
}

func (c *Client) Unsubscribe(id string) bool {
	c.mu.Lock()
	defer c.mu.Unlock()
	_, exists := c.subscriptions[id]
	delete(c.subscriptions, id)
	return exists
}

func credentialEndpoint(uri string) bool {
	return strings.Contains(uri, "/token") || uri == "/riotclient/auth-token" || strings.Contains(uri, "league-session-token")
}

func endpointParams(pattern, uri string) (map[string]any, bool) {
	wanted := strings.Split(strings.Trim(pattern, "/"), "/")
	actual := strings.Split(strings.Trim(uri, "/"), "/")
	if len(wanted) != len(actual) && !strings.HasSuffix(pattern, "/**") {
		return nil, false
	}
	params := map[string]any{}
	for index, part := range wanted {
		if part == "**" {
			return params, true
		}
		if index >= len(actual) {
			return nil, false
		}
		if strings.HasPrefix(part, ":") {
			params[strings.TrimPrefix(part, ":")] = actual[index]
		} else if part != "*" && part != actual[index] {
			return nil, false
		}
	}
	return params, true
}

func (c *Client) dispatchEvent(event map[string]any) {
	uri := String(event["uri"])
	if uri == "" || credentialEndpoint(uri) {
		return
	}
	data := event["data"]
	if String(event["eventType"]) == "Delete" {
		data = nil
	}
	fields := map[string][2]string{
		"/lol-gameflow/v1/gameflow-phase": {"gameflow", "phase"}, "/lol-gameflow/v1/session": {"gameflow", "session"},
		"/lol-champ-select/v1/session": {"champSelect", "session"}, "/lol-champ-select/v1/skin-selector-info": {"champSelect", "skinSelectorInfo"},
		"/lol-lobby/v2/lobby": {"lobby", "lobby"}, "/lol-summoner/v1/current-summoner": {"summoner", "me"},
		"/lol-matchmaking/v1/ready-check": {"matchmaking", "readyCheck"}, "/lol-matchmaking/v1/search": {"matchmaking", "search"},
		"/lol-honor-v2/v1/ballot": {"honor", "ballot"},
	}
	if field, ok := fields[uri]; ok {
		c.set(field[0], field[1], data)
	}
	if c.emit == nil {
		return
	}
	c.mu.RLock()
	handler := c.onEvent
	c.mu.RUnlock()
	if handler != nil {
		handler(uri, String(event["eventType"]), event["data"])
	}
	c.mu.RLock()
	subscriptions := map[string]string{}
	for id, path := range c.subscriptions {
		subscriptions[id] = path
	}
	c.mu.RUnlock()
	for id, pattern := range subscriptions {
		if params, ok := endpointParams(pattern, uri); ok {
			c.emit("league-client-main", "extra-lcu-event", id, event, params)
		}
	}
}

func (c *Client) eventLoop(ctx context.Context) {
	for ctx.Err() == nil {
		_ = c.consumeEvents(ctx)
		timer := time.NewTimer(2 * time.Second)
		select {
		case <-ctx.Done():
			timer.Stop()
			return
		case <-timer.C:
		}
	}
}

// LCU uses one WAMP topic over a local WebSocket. The read-only stream subscribes
// to that topic and forwards only endpoint events requested by renderer features.
func (c *Client) consumeEvents(ctx context.Context) error {
	c.mu.RLock()
	var auth *Auth
	if c.auth != nil {
		copy := *c.auth
		auth = &copy
	}
	c.mu.RUnlock()
	if auth == nil {
		return errors.New("client not connected")
	}
	base := auth.BaseURL
	if base == "" {
		base = fmt.Sprintf("https://127.0.0.1:%d", auth.Port)
	}
	random := make([]byte, 16)
	if _, err := rand.Read(random); err != nil {
		return err
	}
	key := base64.StdEncoding.EncodeToString(random)
	req, err := http.NewRequestWithContext(ctx, http.MethodGet, strings.TrimRight(base, "/")+"/", nil)
	if err != nil {
		return err
	}
	req.SetBasicAuth("riot", auth.Password)
	req.Header.Set("Connection", "Upgrade")
	req.Header.Set("Upgrade", "websocket")
	req.Header.Set("Sec-WebSocket-Version", "13")
	req.Header.Set("Sec-WebSocket-Key", key)
	streamClient := &http.Client{Transport: c.options.HTTPClient.Transport}
	response, err := streamClient.Do(req)
	if err != nil {
		return err
	}
	defer response.Body.Close()
	if response.StatusCode != http.StatusSwitchingProtocols {
		return errors.New("LCU WebSocket unavailable")
	}
	accept := sha1.Sum([]byte(key + "258EAFA5-E914-47DA-95CA-C5AB0DC85B11"))
	if response.Header.Get("Sec-WebSocket-Accept") != base64.StdEncoding.EncodeToString(accept[:]) {
		return errors.New("invalid LCU WebSocket handshake")
	}
	conn, ok := response.Body.(io.ReadWriteCloser)
	if !ok {
		return errors.New("LCU stream is not writable")
	}
	stop := context.AfterFunc(ctx, func() { _ = conn.Close() })
	defer stop()
	if err := writeFrame(conn, 1, []byte(`[5,"OnJsonApiEvent"]`)); err != nil {
		return err
	}
	c.mu.Lock()
	c.eventsConnected = true
	c.mu.Unlock()
	defer func() { c.mu.Lock(); c.eventsConnected = false; c.mu.Unlock() }()
	var message []byte
	for {
		opcode, final, payload, err := readFrame(conn)
		if err != nil {
			return err
		}
		switch opcode {
		case 8:
			return io.EOF
		case 9:
			if err := writeFrame(conn, 10, payload); err != nil {
				return err
			}
			continue
		case 10:
			continue
		case 1:
			message = payload
		case 0:
			message = append(message, payload...)
		default:
			continue
		}
		if len(message) > 16*1024*1024 {
			return errors.New("LCU event exceeds supported size")
		}
		if final {
			var packet []any
			if json.Unmarshal(message, &packet) == nil && len(packet) == 3 && Number(packet[0]) == 8 {
				c.dispatchEvent(Map(packet[2]))
			}
			message = nil
		}
	}
}

func writeFrame(writer io.Writer, opcode byte, payload []byte) error {
	header := []byte{0x80 | opcode}
	size := len(payload)
	if size < 126 {
		header = append(header, 0x80|byte(size))
	} else if size <= 65535 {
		header = append(header, 0x80|126, byte(size>>8), byte(size))
	} else {
		header = append(header, 0x80|127)
		length := make([]byte, 8)
		binary.BigEndian.PutUint64(length, uint64(size))
		header = append(header, length...)
	}
	mask := make([]byte, 4)
	if _, err := rand.Read(mask); err != nil {
		return err
	}
	header = append(header, mask...)
	masked := make([]byte, size)
	for index, value := range payload {
		masked[index] = value ^ mask[index%4]
	}
	if _, err := writer.Write(header); err != nil {
		return err
	}
	_, err := writer.Write(masked)
	return err
}

func readFrame(reader io.Reader) (byte, bool, []byte, error) {
	header := make([]byte, 2)
	if _, err := io.ReadFull(reader, header); err != nil {
		return 0, false, nil, err
	}
	size := uint64(header[1] & 0x7f)
	if size == 126 {
		length := make([]byte, 2)
		if _, err := io.ReadFull(reader, length); err != nil {
			return 0, false, nil, err
		}
		size = uint64(binary.BigEndian.Uint16(length))
	} else if size == 127 {
		length := make([]byte, 8)
		if _, err := io.ReadFull(reader, length); err != nil {
			return 0, false, nil, err
		}
		size = binary.BigEndian.Uint64(length)
	}
	if size > 16*1024*1024 {
		return 0, false, nil, errors.New("LCU frame exceeds supported size")
	}
	mask := make([]byte, 4)
	if header[1]&0x80 != 0 {
		if _, err := io.ReadFull(reader, mask); err != nil {
			return 0, false, nil, err
		}
	}
	payload := make([]byte, int(size))
	if _, err := io.ReadFull(reader, payload); err != nil {
		return 0, false, nil, err
	}
	if header[1]&0x80 != 0 {
		for index := range payload {
			payload[index] ^= mask[index%4]
		}
	}
	return header[0] & 0xf, header[0]&0x80 != 0, payload, nil
}
