// Quasar's worker-owned WebRTC protocol endpoint. No human identity is accepted here.
package main

import (
	"context"
	"crypto/subtle"
	"encoding/json"
	"errors"
	"fmt"
	"io"
	"net"
	"net/http"
	"os"
	"strings"
	"sync"
	"sync/atomic"
	"time"

	"github.com/coder/websocket"
	"github.com/pion/webrtc/v4"
)

const maximumPeers = 64
const maximumMessage = 66 * 1024 // 64 KiB request JSON plus its fixed envelope.
const bulkMessage = 16*1024 + 8
const sendWindow = 256 * 1024

type endpoint struct {
	mu    sync.Mutex
	peers map[string]*peer
	api   *webrtc.API
}

type peer struct {
	pc              *webrtc.PeerConnection
	mu              sync.Mutex
	expires         time.Time
	channels        map[string]*webrtc.DataChannel
	ctx             context.Context
	cancel          context.CancelFunc
	retiring        sync.Once
	bridges         map[string]*bridge
	received        atomic.Uint64
	sent            atomic.Uint64
	maximumBuffered atomic.Uint64
}

type bridge struct {
	dc       *webrtc.DataChannel
	ready    chan struct{}
	queue    chan webrtc.DataChannelMessage
	low      chan struct{}
	attached bool // guarded by peer.mu
}

func (e *endpoint) remove(id string) {
	e.mu.Lock()
	p := e.peers[id]
	delete(e.peers, id)
	e.mu.Unlock()
	if p != nil {
		p.cancel()
		_ = p.pc.Close()
	}
}

func (e *endpoint) retire(id string, p *peer) { p.retiring.Do(func() { p.cancel(); go e.remove(id) }) }

func (e *endpoint) connect(w http.ResponseWriter, r *http.Request, id string, p *peer, name string) {
	if name != "control" && name != "bulk" {
		http.NotFound(w, r)
		return
	}
	p.mu.Lock()
	b := p.bridges[name]
	if b.attached || !time.Now().Before(p.expires) {
		p.mu.Unlock()
		http.Error(w, "peer channel unavailable", 409)
		return
	}
	b.attached = true
	p.mu.Unlock()
	select {
	case <-b.ready:
	case <-p.ctx.Done():
		http.NotFound(w, r)
		return
	case <-time.After(5 * time.Second):
		e.retire(id, p)
		http.Error(w, "channel deadline", 504)
		return
	}
	ws, err := websocket.Accept(w, r, &websocket.AcceptOptions{Subprotocols: []string{"quasar.peer.v1"}, CompressionMode: websocket.CompressionDisabled})
	if err != nil {
		e.retire(id, p)
		return
	}
	defer ws.CloseNow()
	ws.SetReadLimit(maximumMessage)
	finished := make(chan error, 2)
	go func() {
		for {
			kind, data, err := ws.Read(p.ctx)
			if err != nil {
				finished <- err
				return
			}
			if name == "control" && (kind != websocket.MessageText || len(data) > maximumMessage) ||
				name == "bulk" && (kind != websocket.MessageBinary || len(data) > bulkMessage) {
				finished <- errors.New("invalid IPC frame")
				return
			}
			for b.dc.BufferedAmount()+uint64(len(data)) > sendWindow {
				select {
				case <-b.low:
				case <-p.ctx.Done():
					finished <- p.ctx.Err()
					return
				}
			}
			if name == "control" {
				err = b.dc.SendText(string(data))
			} else {
				err = b.dc.Send(data)
			}
			if err != nil {
				finished <- err
				return
			}
			p.sent.Add(uint64(len(data)))
			for old, next := p.maximumBuffered.Load(), b.dc.BufferedAmount(); next > old; old = p.maximumBuffered.Load() {
				if p.maximumBuffered.CompareAndSwap(old, next) {
					break
				}
			}
		}
	}()
	go func() {
		for {
			select {
			case <-p.ctx.Done():
				finished <- p.ctx.Err()
				return
			case message := <-b.queue:
				kind := websocket.MessageBinary
				if message.IsString {
					kind = websocket.MessageText
				}
				if err := ws.Write(p.ctx, kind, message.Data); err != nil {
					finished <- err
					return
				}
			}
		}
	}()
	<-finished
	e.retire(id, p)
	<-finished
}

func identifier(value string) bool {
	if len(value) != 32 {
		return false
	}
	for _, c := range value {
		if c < '0' || c > '9' && c < 'a' || c > 'f' {
			return false
		}
	}
	return true
}

func (e *endpoint) serve(w http.ResponseWriter, r *http.Request) {
	w.Header().Set("Cache-Control", "no-store")
	parts := strings.Split(strings.Trim(r.URL.Path, "/"), "/")
	if len(parts) < 2 || parts[0] != "peers" || !identifier(parts[1]) {
		http.NotFound(w, r)
		return
	}
	id := parts[1]
	if r.Method == "POST" && len(parts) == 2 {
		var offer webrtc.SessionDescription
		r.Body = http.MaxBytesReader(w, r.Body, maximumMessage)
		if json.NewDecoder(r.Body).Decode(&offer) != nil || offer.Type != webrtc.SDPTypeOffer {
			http.Error(w, "invalid offer", 400)
			return
		}
		e.mu.Lock()
		if _, exists := e.peers[id]; exists {
			e.mu.Unlock()
			http.Error(w, "peer already exists", 409)
			return
		}
		if len(e.peers) >= maximumPeers {
			e.mu.Unlock()
			http.Error(w, "peer limit", 429)
			return
		}
		pc, err := e.api.NewPeerConnection(webrtc.Configuration{}) // Direct candidates only; no TURN credentials.
		if err != nil {
			e.mu.Unlock()
			http.Error(w, "cannot create peer", 503)
			return
		}
		ctx, cancel := context.WithCancel(context.Background())
		p := &peer{pc: pc, expires: time.Now().Add(30 * time.Second), channels: map[string]*webrtc.DataChannel{}, ctx: ctx, cancel: cancel,
			bridges: map[string]*bridge{
				"control": {ready: make(chan struct{}), queue: make(chan webrtc.DataChannelMessage, 8), low: make(chan struct{}, 1)},
				"bulk":    {ready: make(chan struct{}), queue: make(chan webrtc.DataChannelMessage, 16), low: make(chan struct{}, 1)},
			}}
		e.peers[id] = p
		e.mu.Unlock()
		pc.OnConnectionStateChange(func(state webrtc.PeerConnectionState) {
			if state == webrtc.PeerConnectionStateFailed {
				e.retire(id, p)
			}
		})
		pc.OnDataChannel(func(dc *webrtc.DataChannel) {
			p.mu.Lock()
			valid := (dc.Label() == "quasar.control.v1" || dc.Label() == "quasar.bulk.v1") &&
				dc.Protocol() == "quasar-sdk.v1" && dc.Ordered() && dc.MaxRetransmits() == nil && dc.MaxPacketLifeTime() == nil
			_, duplicate := p.channels[dc.Label()]
			if valid && !duplicate {
				p.channels[dc.Label()] = dc
				name := "control"
				if dc.Label() == "quasar.bulk.v1" {
					name = "bulk"
				}
				p.bridges[name].dc = dc
			}
			p.mu.Unlock()
			if !valid || duplicate {
				e.retire(id, p)
				return
			}
			name := "control"
			if dc.Label() == "quasar.bulk.v1" {
				name = "bulk"
			}
			b := p.bridges[name]
			dc.OnOpen(func() { close(b.ready) })
			dc.SetBufferedAmountLowThreshold(sendWindow / 2)
			dc.OnBufferedAmountLow(func() {
				select {
				case b.low <- struct{}{}:
				default:
				}
			})
			dc.OnMessage(func(message webrtc.DataChannelMessage) {
				if p.ctx.Err() != nil {
					return
				}
				if name == "control" && (!message.IsString || len(message.Data) > maximumMessage) ||
					name == "bulk" && (message.IsString || len(message.Data) < 8 || len(message.Data) > bulkMessage) {
					e.retire(id, p)
					return
				}
				p.received.Add(uint64(len(message.Data)))
				select {
				case b.queue <- message:
				default:
					e.retire(id, p)
				}
			})
			dc.OnClose(func() { e.retire(id, p) })
		})
		if pc.SetRemoteDescription(offer) != nil {
			e.remove(id)
			http.Error(w, "invalid SDP", 400)
			return
		}
		answer, err := pc.CreateAnswer(nil)
		if err != nil {
			e.remove(id)
			http.Error(w, "cannot answer", 400)
			return
		}
		gathered := webrtc.GatheringCompletePromise(pc)
		if pc.SetLocalDescription(answer) != nil {
			e.remove(id)
			http.Error(w, "cannot set answer", 400)
			return
		}
		select {
		case <-gathered:
		case <-r.Context().Done():
			e.remove(id)
			return
		case <-time.After(10 * time.Second):
			e.remove(id)
			http.Error(w, "ICE gathering deadline", 504)
			return
		}
		w.Header().Set("Content-Type", "application/json")
		_ = json.NewEncoder(w).Encode(pc.LocalDescription())
		return
	}
	e.mu.Lock()
	p := e.peers[id]
	e.mu.Unlock()
	if p == nil {
		http.NotFound(w, r)
		return
	}
	if len(parts) == 4 && parts[2] == "channels" && r.Method == "GET" {
		e.connect(w, r, id, p, parts[3])
		return
	}
	if len(parts) == 2 && r.Method == "DELETE" {
		e.remove(id)
		w.WriteHeader(204)
		return
	}
	if len(parts) == 3 && parts[2] == "pulse" && r.Method == "POST" {
		p.mu.Lock()
		if !time.Now().Before(p.expires) {
			p.mu.Unlock()
			e.remove(id)
			http.NotFound(w, r)
			return
		}
		p.expires = time.Now().Add(5 * time.Second)
		p.mu.Unlock()
		w.WriteHeader(204)
		return
	}
	if len(parts) == 2 && r.Method == "GET" {
		status := map[string]any{"state": p.pc.ConnectionState().String(), "path": "unavailable",
			"receivedBytes": p.received.Load(), "sentBytes": p.sent.Load(), "maximumBufferedBytes": p.maximumBuffered.Load()}
		if transport := p.pc.SCTP(); transport != nil && transport.Transport() != nil {
			if pair, err := transport.Transport().ICETransport().GetSelectedCandidatePair(); err == nil && pair != nil {
				status["localCandidateType"] = pair.Local.Typ.String()
				status["remoteCandidateType"] = pair.Remote.Typ.String()
				if pair.Local.Typ == webrtc.ICECandidateTypeRelay || pair.Remote.Typ == webrtc.ICECandidateTypeRelay {
					status["path"] = "relayed"
				} else {
					status["path"] = "direct"
				}
			}
		}
		w.Header().Set("Content-Type", "application/json")
		_ = json.NewEncoder(w).Encode(status)
		return
	}
	http.NotFound(w, r)
}

func run() error {
	secret := os.Getenv("QUASAR_PEER_TOKEN")
	if len(secret) != 64 {
		return errors.New("private worker token is required")
	}
	listener, err := net.Listen("tcp4", "127.0.0.1:0")
	if err != nil {
		return err
	}
	defer listener.Close()
	settings := webrtc.SettingEngine{}
	settings.SetIncludeLoopbackCandidate(true)
	settings.SetSCTPMaxMessageSize(maximumMessage)
	settings.SetSCTPMaxReceiveBufferSize(256 * 1024)
	e := &endpoint{peers: map[string]*peer{}, api: webrtc.NewAPI(webrtc.WithSettingEngine(settings))}
	server := &http.Server{ReadHeaderTimeout: 3 * time.Second, ReadTimeout: 15 * time.Second, WriteTimeout: 15 * time.Second, MaxHeaderBytes: 4096,
		Handler: http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
			if subtle.ConstantTimeCompare([]byte(r.Header.Get("Authorization")), []byte("Bearer "+secret)) != 1 {
				http.Error(w, "unauthorized", 401)
				return
			}
			e.serve(w, r)
		})}
	done := make(chan error, 1)
	go func() { done <- server.Serve(listener) }()
	go func() { _, _ = io.Copy(io.Discard, os.Stdin); done <- nil }()
	stop := make(chan struct{})
	go func() {
		timer := time.NewTicker(500 * time.Millisecond)
		defer timer.Stop()
		for {
			select {
			case <-stop:
				return
			case <-timer.C:
				e.mu.Lock()
				var expired []string
				for id, p := range e.peers {
					p.mu.Lock()
					if !time.Now().Before(p.expires) {
						expired = append(expired, id)
					}
					p.mu.Unlock()
				}
				e.mu.Unlock()
				for _, id := range expired {
					e.remove(id)
				}
			}
		}
	}()
	_ = json.NewEncoder(os.Stdout).Encode(map[string]any{"version": 1, "url": "http://" + listener.Addr().String()})
	err = <-done
	close(stop)
	_ = server.Close()
	e.mu.Lock()
	ids := make([]string, 0, len(e.peers))
	for id := range e.peers {
		ids = append(ids, id)
	}
	e.mu.Unlock()
	for _, id := range ids {
		e.remove(id)
	}
	if errors.Is(err, http.ErrServerClosed) {
		return nil
	}
	return err
}

func main() {
	if err := run(); err != nil {
		fmt.Fprintln(os.Stderr, "Quasar peer stopped:", err)
		os.Exit(1)
	}
}
