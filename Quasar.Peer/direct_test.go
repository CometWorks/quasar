package main

import (
	"bytes"
	"context"
	"encoding/json"
	"fmt"
	"net"
	"net/http/httptest"
	"net/netip"
	"strconv"
	"strings"
	"testing"
	"time"

	"github.com/pion/logging"
	"github.com/pion/stun/v4"
	"github.com/pion/webrtc/v4"
)

func testDirectEndpoint(t *testing.T, public string) *endpoint {
	t.Helper()
	logs := logging.NewDefaultLoggerFactory()
	settings := webrtc.SettingEngine{}
	settings.SetIncludeLoopbackCandidate(true)
	settings.SetSCTPMaxMessageSize(maximumMessage)
	direct, err := openDirectPort(0, public, logs)
	if err != nil {
		t.Fatal(err)
	}
	t.Cleanup(func() { _ = direct.mux.Close() })
	return &endpoint{peers: map[string]*peer{}, api: webrtc.NewAPI(webrtc.WithSettingEngine(settings)), settings: settings, direct: direct}
}

// Every native candidate uses the opened port, and the public address is
// advertised beside local ones. A browser then connects through that port.
func TestFixedPortCarriesTheConnectionAndAdvertisesThePublicAddress(t *testing.T) {
	e := testDirectEndpoint(t, "203.0.113.7")
	settings := webrtc.SettingEngine{}
	settings.SetIncludeLoopbackCandidate(true)
	browser, err := webrtc.NewAPI(webrtc.WithSettingEngine(settings)).NewPeerConnection(webrtc.Configuration{})
	if err != nil {
		t.Fatal(err)
	}
	defer browser.Close()
	control, err := browser.CreateDataChannel("quasar.control.v1", &webrtc.DataChannelInit{Protocol: ptr("quasar-sdk.v1")})
	if err != nil {
		t.Fatal(err)
	}
	opened := make(chan struct{})
	control.OnOpen(func() { close(opened) })
	offer, err := browser.CreateOffer(nil)
	if err != nil {
		t.Fatal(err)
	}
	gathered := webrtc.GatheringCompletePromise(browser)
	if err := browser.SetLocalDescription(offer); err != nil {
		t.Fatal(err)
	}
	<-gathered
	id := strings.Repeat("b", 32)
	defer e.remove(id)
	// A known public address replaces random-port STUN discovery.
	body, _ := json.Marshal(map[string]any{"type": "offer", "sdp": browser.LocalDescription().SDP,
		"iceServers": []map[string]any{{"urls": []string{"stun:192.0.2.1:3478"}}}})
	response := httptest.NewRecorder()
	e.serve(response, httptest.NewRequest("POST", "/peers/"+id, bytes.NewReader(body)))
	if response.Code != 200 {
		t.Fatalf("answer: %d %s", response.Code, response.Body.String())
	}
	var answer webrtc.SessionDescription
	if err := json.Unmarshal(response.Body.Bytes(), &answer); err != nil {
		t.Fatal(err)
	}
	port := strconv.Itoa(e.direct.port)
	public, local := false, false
	for _, line := range strings.Split(answer.SDP, "\n") {
		value, ok := strings.CutPrefix(strings.TrimSpace(line), "a=candidate:")
		if !ok {
			continue
		}
		fields := strings.Fields(value)
		if fields[2] != "udp" || fields[5] != port || fields[7] != "host" {
			t.Fatalf("candidate outside the fixed port: %s", value)
		}
		public = public || fields[4] == "203.0.113.7"
		local = local || fields[4] != "203.0.113.7"
	}
	if !public || !local {
		t.Fatalf("public %v local %v in %s", public, local, answer.SDP)
	}
	if err := browser.SetRemoteDescription(answer); err != nil {
		t.Fatal(err)
	}
	select {
	case <-opened:
	case <-time.After(10 * time.Second):
		t.Fatal("browser did not connect through the fixed port")
	}
}

func ptr[T any](value T) *T { return &value }

// fakeDiscovery answers Binding requests and, when asked, sends the probe to the
// sender's address at the requested port, as Nebula's STUN service does.
func fakeDiscovery(t *testing.T, sendProbe bool) string {
	t.Helper()
	socket, err := net.ListenUDP("udp4", &net.UDPAddr{IP: net.IPv4(127, 0, 0, 1)})
	if err != nil {
		t.Fatal(err)
	}
	t.Cleanup(func() { socket.Close() })
	go func() {
		var buffer [1500]byte
		for {
			n, source, err := socket.ReadFromUDPAddrPort(buffer[:])
			if err != nil {
				return
			}
			request := stun.Message{Raw: append([]byte{}, buffer[:n]...)}
			if request.Decode() != nil {
				continue
			}
			mapped := stun.XORMappedAddress{IP: source.Addr().AsSlice(), Port: int(source.Port())}
			reply := stun.MustBuild(&request, stun.BindingSuccess, &mapped, stun.Fingerprint)
			_, _ = socket.WriteToUDPAddrPort(reply.Raw, source)
			value, err := request.Get(probeAttribute)
			if sendProbe && err == nil && len(value) == probeValueLength {
				port := uint16(value[0])<<8 | uint16(value[1])
				_, _ = socket.WriteToUDPAddrPort(append(append([]byte{}, probeMagic...), value[4:20]...), netip.AddrPortFrom(source.Addr(), port))
			}
		}
	}()
	return socket.LocalAddr().String()
}

func TestProbeReportsAnOpenPort(t *testing.T) {
	e := testDirectEndpoint(t, "")
	body := fmt.Sprintf(`{"server":%q}`, "localhost:"+strings.Split(fakeDiscovery(t, true), ":")[1])
	response := httptest.NewRecorder()
	e.serve(response, httptest.NewRequest("POST", "/direct/probe", strings.NewReader(body)))
	var result probeResult
	if response.Code != 200 || json.Unmarshal(response.Body.Bytes(), &result) != nil {
		t.Fatalf("probe: %d %s", response.Code, response.Body.String())
	}
	if !result.Reachable || result.Address != "127.0.0.1" || result.Public || result.Port != e.direct.port {
		t.Fatalf("unexpected probe result %+v", result)
	}
	// A private observed address is reported but never advertised.
	if e.direct.publicIP().IsValid() {
		t.Fatal("private address advertised")
	}
}

func TestProbeReportsAClosedPort(t *testing.T) {
	e := testDirectEndpoint(t, "")
	result := e.direct.probe(context.Background(), fakeDiscovery(t, false))
	if result.Reachable || result.Address != "127.0.0.1" || result.Error != "" {
		t.Fatalf("unexpected probe result %+v", result)
	}
	silent, err := net.ListenUDP("udp4", &net.UDPAddr{IP: net.IPv4(127, 0, 0, 1)})
	if err != nil {
		t.Fatal(err)
	}
	defer silent.Close()
	if result := e.direct.probe(context.Background(), silent.LocalAddr().String()); result.Reachable || result.Error == "" {
		t.Fatalf("silent discovery reported %+v", result)
	}
}

func TestProbeWithoutFixedPortIsUnavailable(t *testing.T) {
	e := &endpoint{peers: map[string]*peer{}, api: webrtc.NewAPI()}
	response := httptest.NewRecorder()
	e.serve(response, httptest.NewRequest("POST", "/direct/probe", strings.NewReader(`{"server":"127.0.0.1:3478"}`)))
	if response.Code != 409 {
		t.Fatalf("probe without a fixed port returned %d", response.Code)
	}
}

func TestPublicAddressExcludesPrivateRanges(t *testing.T) {
	for address, public := range map[string]bool{"203.0.113.7": true, "8.8.8.8": true, "192.168.1.15": false, "10.0.0.1": false,
		"100.64.1.1": false, "127.0.0.1": false, "169.254.1.1": false, "2001:db8::1": false} {
		if publicAddress(netip.MustParseAddr(address)) != public {
			t.Fatalf("%s public = %v", address, !public)
		}
	}
}
