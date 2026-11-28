// Optional fixed UDP port for hosted direct connections, plus its external reachability probe.
package main

import (
	"bytes"
	"context"
	"crypto/rand"
	"encoding/binary"
	"encoding/json"
	"errors"
	"net"
	"net/http"
	"net/netip"
	"os"
	"strconv"
	"strings"
	"sync"
	"time"

	"github.com/pion/ice/v4"
	"github.com/pion/logging"
	"github.com/pion/stun/v4"
	"github.com/pion/webrtc/v4"
)

// probeAttribute asks Nebula's STUN service to send one unsolicited datagram to the
// sender's public IP at the requested port. It is comprehension-optional, so plain
// STUN servers answer the Binding request and ignore it.
const probeAttribute = stun.AttrType(0xC051)
const probeValueLength = 68 // port, reserved, nonce, padding: never smaller than the probe it causes.
const maximumProbes = 4

// The probe is deliberately not STUN (first byte 0x51), so the ICE mux hands it to
// OnUnhandledPacket instead of looking for an ICE username.
var probeMagic = []byte("QUASAR-PROBE-1\x00\x00")

type directPort struct {
	port   int
	mux    ice.UDPMux
	mu     sync.Mutex
	public netip.Addr
	probes map[[16]byte]chan struct{}
}

func openDirectPort(port int, public string, logs logging.LoggerFactory) (*directPort, error) {
	socket, err := net.ListenUDP("udp4", &net.UDPAddr{Port: port})
	if err != nil {
		return nil, err
	}
	d := &directPort{port: socket.LocalAddr().(*net.UDPAddr).Port, probes: map[[16]byte]chan struct{}{}}
	if address, err := netip.ParseAddr(public); err == nil && publicAddress(address) {
		d.public = address
	}
	d.mux = ice.NewUDPMuxDefault(ice.UDPMuxParams{UDPConn: socket, Logger: logs.NewLogger("ice"), OnUnhandledPacket: d.unhandled})
	return d, nil
}

func (d *directPort) unhandled(data []byte, _ netip.AddrPort) {
	if len(data) != 32 || !bytes.Equal(data[:16], probeMagic) {
		return
	}
	var nonce [16]byte
	copy(nonce[:], data[16:])
	d.mu.Lock()
	if done, ok := d.probes[nonce]; ok {
		delete(d.probes, nonce)
		close(done)
	}
	d.mu.Unlock()
}

func (d *directPort) publicIP() netip.Addr {
	d.mu.Lock()
	defer d.mu.Unlock()
	return d.public
}

// settings returns an engine for one peer. The mux carries every UDP candidate, so
// a single opened firewall port is enough. A known public IP is advertised beside
// the local addresses, which keeps LAN browsers working too.
func (d *directPort) settings(base webrtc.SettingEngine) (webrtc.SettingEngine, bool) {
	base.SetICEUDPMux(d.mux)
	base.SetNetworkTypes([]webrtc.NetworkType{webrtc.NetworkTypeUDP4})
	public := d.publicIP()
	if !public.IsValid() {
		return base, false
	}
	if base.SetICEAddressRewriteRules(webrtc.ICEAddressRewriteRule{External: []string{public.String()},
		AsCandidateType: webrtc.ICECandidateTypeHost, Mode: webrtc.ICEAddressRewriteAppend}) != nil {
		return base, false
	}
	return base, true
}

func publicAddress(address netip.Addr) bool {
	address = address.Unmap()
	return address.Is4() && address.IsGlobalUnicast() && !address.IsPrivate() &&
		!netip.MustParsePrefix("100.64.0.0/10").Contains(address)
}

type probeResult struct {
	Reachable bool   `json:"reachable"`
	Address   string `json:"address,omitempty"`
	Port      int    `json:"port"`
	Public    bool   `json:"public"`
	Error     string `json:"error,omitempty"`
}

// probe asks the STUN service from a separate socket, so no NAT or firewall state
// exists for the fixed port. Only an inbound path opened by the operator delivers it.
func (d *directPort) probe(ctx context.Context, server string) probeResult {
	result := probeResult{Port: d.port}
	var nonce [16]byte
	if _, err := rand.Read(nonce[:]); err != nil {
		result.Error = "random source unavailable"
		return result
	}
	done := make(chan struct{})
	d.mu.Lock()
	if len(d.probes) >= maximumProbes {
		d.mu.Unlock()
		result.Error = "probe already running"
		return result
	}
	d.probes[nonce] = done
	d.mu.Unlock()
	defer func() { d.mu.Lock(); delete(d.probes, nonce); d.mu.Unlock() }()

	ctx, cancel := context.WithTimeout(ctx, 4*time.Second)
	defer cancel()
	target, err := (&net.Resolver{}).LookupNetIP(ctx, "ip4", strings.Split(server, ":")[0])
	_, portText, splitErr := net.SplitHostPort(server)
	port, portErr := strconv.Atoi(portText)
	if err != nil || len(target) == 0 || splitErr != nil || portErr != nil || port < 1 || port > 65535 {
		result.Error = "discovery service unavailable"
		return result
	}
	socket, err := net.ListenUDP("udp4", nil)
	if err != nil {
		result.Error = "cannot open probe socket"
		return result
	}
	defer socket.Close()
	value := make([]byte, probeValueLength)
	binary.BigEndian.PutUint16(value, uint16(d.port))
	copy(value[4:20], nonce[:])
	request, err := stun.Build(stun.TransactionID, stun.BindingRequest, stun.RawAttribute{Type: probeAttribute, Value: value}, stun.Fingerprint)
	if err != nil {
		result.Error = "cannot build probe"
		return result
	}
	destination := net.UDPAddrFromAddrPort(netip.AddrPortFrom(target[0].Unmap(), uint16(port)))
	mapped := make(chan netip.Addr, 1)
	go func() {
		var buffer [1500]byte
		for {
			n, err := socket.Read(buffer[:])
			if err != nil {
				return
			}
			response := stun.Message{Raw: append([]byte{}, buffer[:n]...)}
			var address stun.XORMappedAddress
			if response.Decode() != nil || response.Type != stun.BindingSuccess || response.TransactionID != request.TransactionID ||
				address.GetFrom(&response) != nil {
				continue
			}
			if ip, ok := netip.AddrFromSlice(address.IP); ok {
				select {
				case mapped <- ip.Unmap():
				default:
				}
				return
			}
		}
	}()
	// UDP may drop either datagram; repeat the same request a few times.
	retry := time.NewTimer(0)
	defer retry.Stop()
	for attempt := 0; ; {
		select {
		case <-retry.C:
			if attempt < 3 {
				_, _ = socket.WriteToUDP(request.Raw, destination)
				attempt++
				retry.Reset(time.Duration(attempt) * 500 * time.Millisecond)
			}
		case address := <-mapped:
			result.Address, result.Public = address.String(), publicAddress(address)
			if result.Public {
				d.mu.Lock()
				d.public = address
				d.mu.Unlock()
			}
			if result.Reachable {
				return result
			}
		case <-done:
			result.Reachable = true
			if result.Address != "" {
				return result
			}
			done = nil // Wait briefly for the address, which normally arrives first.
		case <-ctx.Done():
			if result.Address == "" && !result.Reachable {
				result.Error = "no answer from the discovery service"
			}
			return result
		}
	}
}

func (e *endpoint) serveDirect(w http.ResponseWriter, r *http.Request) {
	if r.Method != "POST" || r.URL.Path != "/direct/probe" {
		http.NotFound(w, r)
		return
	}
	if e.direct == nil {
		http.Error(w, "fixed direct port is not configured", 409)
		return
	}
	var request struct {
		Server string `json:"server"`
	}
	r.Body = http.MaxBytesReader(w, r.Body, 4096)
	if json.NewDecoder(r.Body).Decode(&request) != nil || len(request.Server) > 256 || !strings.Contains(request.Server, ":") {
		http.Error(w, "invalid probe", 400)
		return
	}
	w.Header().Set("Content-Type", "application/json")
	_ = json.NewEncoder(w).Encode(e.direct.probe(r.Context(), request.Server))
}

// directPortFromEnvironment reads the worker-supplied fixed port. A port that
// cannot be opened leaves the peer on ephemeral ports and reports why.
func directPortFromEnvironment(logs logging.LoggerFactory) (*directPort, string) {
	text := os.Getenv("QUASAR_PEER_UDP_PORT")
	if text == "" {
		return nil, ""
	}
	port, err := strconv.Atoi(text)
	if err != nil || port < 1024 || port > 65535 {
		return nil, "invalid port"
	}
	d, err := openDirectPort(port, os.Getenv("QUASAR_PEER_PUBLIC_IP"), logs)
	if err != nil {
		if errors.Is(err, os.ErrPermission) {
			return nil, "permission denied"
		}
		return nil, "port is already in use or unavailable"
	}
	return d, ""
}
