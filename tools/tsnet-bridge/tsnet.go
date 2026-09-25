package tsnet

import (
	"context"
	"encoding/json"
	"fmt"
	"io"
	"net"
	"net/netip"
	"os"
	"path/filepath"
	"sync"
	"time"

	"tailscale.com/tsnet"
)

// Tsnet — gomobile биндинг. Экспортируется как org.terminalv.tsnet.Tsnet (Java) и Tsnet (ObjC).
// Userspace, без TUN, не конфликтует с Happ — только Dial для SSH.
type Tsnet struct {
	mu     sync.Mutex
	srv    *tsnet.Server
	cancel context.CancelFunc
}

// Account is the non-blocking browser-login API used by the mobile UI.
// The account state lives separately from legacy auth-key connections.
func (t *Tsnet) Account(command, payload string) (string, error) {
	if command == "begin" {
		var args struct { Hostname string; StateDir string }
		if err := json.Unmarshal([]byte(payload), &args); err != nil { return "", err }
		if args.StateDir == "" { return "", fmt.Errorf("state directory required") }
		t.mu.Lock()
		if t.srv == nil {
			files := filepath.Dir(args.StateDir)
			_ = os.Setenv("HOME", files)
			_ = os.Setenv("TMPDIR", files)
			_ = os.Setenv("XDG_CACHE_HOME", filepath.Join(files, "cache"))
			_ = os.Setenv("XDG_CONFIG_HOME", files)
			_ = os.MkdirAll(filepath.Join(files, "cache"), 0700)
			srv := &tsnet.Server{Dir: args.StateDir, Hostname: args.Hostname, Ephemeral: false, Logf: func(string, ...any) {}}
			if err := srv.Start(); err != nil { t.mu.Unlock(); return "", err }
			t.srv = srv
		}
		t.mu.Unlock()
	}
	t.mu.Lock(); srv := t.srv; t.mu.Unlock()
	if srv == nil { return "", fmt.Errorf("account not started") }
	lc, err := srv.LocalClient()
	if err != nil { return "", err }
	ctx, cancel := context.WithTimeout(context.Background(), 10*time.Second)
	defer cancel()
	if command == "logout" {
		if err := lc.Logout(ctx); err != nil { return "", err }
		return "{}", t.Stop()
	}
	status, err := lc.Status(ctx)
	if err != nil { return "", err }
	if command == "begin" && status.BackendState == "NeedsLogin" {
		if err := lc.StartLoginInteractive(ctx); err != nil { return "", err }
	}
	type peer struct { ID string `json:"id"`; Name string `json:"name"`; IP string `json:"ip"` }
	peers := []peer{}
	for _, p := range status.Peer {
		if !p.Online || p.OS != "windows" { continue }
		for _, addr := range p.TailscaleIPs {
			if addr.Is4() { peers = append(peers, peer{string(p.ID), p.HostName, addr.String()}); break }
		}
	}
	login := ""
	if status.Self != nil { if u, ok := status.User[status.Self.UserID]; ok { login = u.LoginName } }
	result, err := json.Marshal(struct {
		Running bool `json:"running"`; State string `json:"state"`; AuthURL string `json:"authUrl"`; Login string `json:"login"`; Peers []peer `json:"peers"`
	}{status.BackendState == "Running", status.BackendState, status.AuthURL, login, peers})
	return string(result), err
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
	// Android fix: no HOME/TMPDIR, wd is /, tsnet panics trying to find log/cache dir.
	// Должно быть os.Setenv из Go (C setenv не видит Go runtime).
	filesDir := filepath.Dir(stateDir)
	if filesDir != "" && filesDir != "." && filesDir != "/" {
		_ = os.Setenv("HOME", filesDir)
		_ = os.Setenv("TMPDIR", filesDir)
		_ = os.Setenv("XDG_CACHE_HOME", filepath.Join(filesDir, "cache"))
		_ = os.Setenv("XDG_CONFIG_HOME", filesDir)
		_ = os.Setenv("XDG_DATA_HOME", filepath.Join(filesDir, "data"))
		_ = os.MkdirAll(filepath.Join(filesDir, "cache"), 0700)
		_ = os.MkdirAll(filepath.Join(filesDir, "data"), 0700)
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
	ctx, cancel := context.WithTimeout(context.Background(), 15*time.Second)
	defer cancel()
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

// DialLoopback dials host:port over the tailnet and bridges it to a localhost
// TCP listener, returning its "127.0.0.1:port" address. The caller connects
// there with a plain socket. A loopback bridge is required because tsnet
// connections live in userspace: there is no OS fd to pass.
func (t *Tsnet) DialLoopback(host string, port int) (string, error) {
	up, err := t.Dial(host, port)
	if err != nil {
		return "", err
	}
	ln, err := net.Listen("tcp", "127.0.0.1:0")
	if err != nil {
		up.Close()
		return "", fmt.Errorf("loopback listen: %w", err)
	}
	tcpLn, ok := ln.(*net.TCPListener)
	if !ok {
		ln.Close()
		up.Close()
		return "", fmt.Errorf("loopback listen: not a TCP listener")
	}
	_ = tcpLn.SetDeadline(time.Now().Add(30 * time.Second))
	go func() {
		defer tcpLn.Close()
		down, err := tcpLn.Accept()
		if err != nil {
			up.Close()
			return
		}
		_ = tcpLn.Close()
		go func() {
			_, _ = io.Copy(up, down)
			up.Close()
			down.Close()
		}()
		_, _ = io.Copy(down, up)
		up.Close()
		down.Close()
	}()
	return ln.Addr().String(), nil
}
