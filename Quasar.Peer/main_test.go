package main

import (
	"bytes"
	"encoding/json"
	"github.com/pion/webrtc/v4"
	"net"
	"net/http/httptest"
	"strings"
	"testing"
	"time"
)

func TestCreditBurstsRetainControlByteAndCountBounds(t *testing.T) {
	b := &bridge{queue: make(chan webrtc.DataChannelMessage, 128), maximumQueued: 8 * maximumMessage}
	credit := webrtc.DataChannelMessage{Data: []byte(`{"v":1,"type":"credit","id":1,"bytes":262144}`), IsString: true}
	for i := 0; i < 64; i++ {
		if !b.enqueue(credit) {
			t.Fatal("valid cumulative credit burst rejected")
		}
	}
	large := webrtc.DataChannelMessage{Data: make([]byte, maximumMessage), IsString: true}
	for i := 0; i < 7; i++ {
		if !b.enqueue(large) {
			t.Fatal("bounded control rejected")
		}
	}
	before := b.queued.Load()
	if b.enqueue(large) || b.queued.Load() != before {
		t.Fatal("byte cap bypassed or rejected frame retained")
	}
	for len(b.queue) > 0 {
		frame := <-b.queue
		b.queued.Add(-int64(len(frame.Data)))
	}
	if b.queued.Load() != 0 {
		t.Fatal("delivered frame retains its byte reservation")
	}
	for i := 0; i < cap(b.queue); i++ {
		if !b.enqueue(credit) {
			t.Fatal("valid count rejected")
		}
	}
	before = b.queued.Load()
	if b.enqueue(credit) || b.queued.Load() != before {
		t.Fatal("message cap bypassed or reservation leaked")
	}
}

// A dead STUN service must not hold the SDP answer behind its timeout.
func TestTrickleAnswersWhileStunIsStillGathering(t *testing.T) {
	blackhole, err := net.ListenPacket("udp4", "127.0.0.1:0")
	if err != nil {
		t.Fatal(err)
	}
	defer blackhole.Close()
	settings := webrtc.SettingEngine{}
	settings.SetIncludeLoopbackCandidate(true)
	api := webrtc.NewAPI(webrtc.WithSettingEngine(settings))
	browser, err := api.NewPeerConnection(webrtc.Configuration{})
	if err != nil {
		t.Fatal(err)
	}
	defer browser.Close()
	if _, err = browser.CreateDataChannel("quasar.control.v1", nil); err != nil {
		t.Fatal(err)
	}
	offer, err := browser.CreateOffer(nil)
	if err != nil {
		t.Fatal(err)
	}
	e := &endpoint{peers: map[string]*peer{}, api: api}
	id := strings.Repeat("a", 32)
	defer e.remove(id)
	body, _ := json.Marshal(map[string]any{"type": "offer", "sdp": offer.SDP, "trickle": true,
		"iceServers": []map[string]any{{"urls": []string{"stun:" + blackhole.LocalAddr().String()}}}})
	answer := httptest.NewRecorder()
	started := time.Now()
	e.serve(answer, httptest.NewRequest("POST", "/peers/"+id, bytes.NewReader(body)))
	if answer.Code != 200 {
		t.Fatalf("answer: %d %s", answer.Code, answer.Body.String())
	}
	if time.Since(started) > time.Second {
		t.Fatal("answer waited for discovery")
	}
	if e.peers[id].pc.ICEGatheringState() == webrtc.ICEGatheringStateComplete {
		t.Fatal("test STUN did not stall discovery")
	}
	for _, update := range []string{
		`{"candidates":[],"offset":-1}`, `{"candidates":[{"candidate":""}]}`,
		`{"candidates":[{"candidate":"` + strings.Repeat("x", 513) + `"}]}`,
	} {
		response := httptest.NewRecorder()
		e.serve(response, httptest.NewRequest("POST", "/peers/"+id+"/ice", strings.NewReader(update)))
		if response.Code != 400 {
			t.Fatalf("invalid update accepted: %d", response.Code)
		}
	}
	// Quotas are cumulative across batches, and completion cannot be reopened.
	p := e.peers[id]
	p.mu.Lock()
	p.remoteCandidates = maximumCandidates
	p.mu.Unlock()
	response := httptest.NewRecorder()
	e.serve(response, httptest.NewRequest("POST", "/peers/"+id+"/ice", strings.NewReader(`{"candidates":[{"candidate":"candidate:x"}]}`)))
	if response.Code != 409 {
		t.Fatal("cumulative ICE cap bypassed")
	}
	p.mu.Lock()
	p.remoteCandidates = 0
	p.remoteComplete = true
	p.mu.Unlock()
	response = httptest.NewRecorder()
	e.serve(response, httptest.NewRequest("POST", "/peers/"+id+"/ice", strings.NewReader(`{"candidates":[{"candidate":"candidate:x"}]}`)))
	if response.Code != 409 {
		t.Fatal("completed ICE reopened")
	}
	response = httptest.NewRecorder()
	e.serve(response, httptest.NewRequest("POST", "/peers/"+id+"/ice", strings.NewReader(`{"candidates":[]}`)))
	if response.Code != 200 {
		t.Fatalf("candidate polling failed: %d", response.Code)
	}
	var result struct {
		Candidates []webrtc.ICECandidateInit
		Complete   bool
	}
	if json.Unmarshal(response.Body.Bytes(), &result) != nil || result.Complete {
		t.Fatal("discovery unexpectedly completed")
	}
}
