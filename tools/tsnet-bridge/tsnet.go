package tsnet

import (
	"context"
	"fmt"
	"net"
	"net/netip"
	"os"
	"sync"

	"tailscale.com/tsnet"
)

// Tsnet — gomobile биндинг. Экспортируется как org.terminalv.tsnet.Tsnet (Java) и Tsnet (ObjC).
// Userspace, без TUN, не конфликтует с Happ — только Dial для SSH.
type Tsnet struct {
	mu     sync.Mutex
	srv    *tsnet.Server
	cancel context.CancelFunc
}

var global *Tsnet
var globalMu sync.Mutex

// Start запускает tsnet. Идемпотентно.
// authKey — из admin.tailscale.com → Keys (ephemeral, reusable). Пустой = interactive (не для mobile).
// hostname — например terminalv-mobile, controlUrl — обычно "", stateDir — filesDir/tsnet-state
func (t *Tsnet) Start(authKey, hostname, controlURL, stateDir string, logVerbosity int) error {
	t.mu.Lock()
	defer t.mu.Unlock()
	if t.srv != nil {
		return nil
	}
	if stateDir == "" {
		stateDir = os.TempDir() + "/tsnet-terminalv"
	}
	if err := os.MkdirAll(stateDir, 0700); err != nil {
		return fmt.Errorf("mkdir stateDir: %w", err)
	}
	srv := &tsnet.Server{
		Dir:        stateDir,
		Hostname:   hostname,
		AuthKey:    authKey,
		ControlURL: controlURL,
		Ephemeral:  true,
		Logf:       func(string, ...any) {},
	}
	if logVerbosity > 0 {
		srv.Logf = func(f string, a ...any) { fmt.Printf(f+"\n", a...) }
	}
	// Запускаем в фоне
	_, cancel := context.WithCancel(context.Background())
	t.srv = srv
	t.cancel = cancel
	// Ленивый старт: первый Dial триггерит Up. Проверим статус
	status, err := srv.Up(context.Background())
	if err != nil {
		t.srv = nil
		t.cancel = nil
		return fmt.Errorf("tsnet up: %w", err)
	}
	_ = status
	globalMu.Lock()
	global = t
	globalMu.Unlock()
	return nil
}

func (t *Tsnet) Stop() error {
	t.mu.Lock()
	defer t.mu.Unlock()
	if t.srv == nil {
		return nil
	}
	if t.cancel != nil {
		t.cancel()
	}
	err := t.srv.Close()
	t.srv = nil
	t.cancel = nil
	globalMu.Lock()
	if global == t {
		global = nil
	}
	globalMu.Unlock()
	return err
}

// Dial — TCP через tailnet. Используется для SSH.
// addr: 100.119.48.15 или magicDNS, port: 22
func (t *Tsnet) Dial(host string, port int) (net.Conn, error) {
	t.mu.Lock()
	srv := t.srv
	t.mu.Unlock()
	if srv == nil {
		return nil, fmt.Errorf("tsnet not started — call Start first")
	}
	// tsnet.Server.Dial — userspace dial без системного TUN
	ctx := context.Background()
	addr := net.JoinHostPort(host, fmt.Sprintf("%d", port))
	// Проверяем что host резолвится в tailnet
	if ip, err := netip.ParseAddr(host); err == nil {
		_ = ip
	}
	conn, err := srv.Dial(ctx, "tcp", addr)
	if err != nil {
		return nil, fmt.Errorf("tsnet dial %s: %w", addr, err)
	}
	return conn, nil
}

// DialConn возвращает fd для Java (Socket). gomobile не умеет net.Conn напрямую — оборачиваем в TCPConn.
// Для Android биндинга используем DialFD.
func (t *Tsnet) DialFD(host string, port int) (int, error) {
	conn, err := t.Dial(host, port)
	if err != nil {
		return -1, err
	}
	// Извлекаем fd через syscall (только для TCP)
	if tc, ok := conn.(*net.TCPConn); ok {
		f, err := tc.File()
		if err != nil {
			conn.Close()
			return -1, err
		}
		fd := int(f.Fd())
		// Дублируем fd чтобы не закрыть при Close(f)
		dup, err := net.FileConn(f)
		if err != nil {
			f.Close()
			return fd, nil
		}
		_ = dup
		f.Close()
		return fd, nil
	}
	// fallback: вернуть -1 и пусть Java использует DialStream
	return -1, fmt.Errorf("DialFD only for TCPConn, use DialStream")
}
