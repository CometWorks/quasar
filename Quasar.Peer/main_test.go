package main

import (
	"github.com/pion/webrtc/v4"
	"testing"
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
