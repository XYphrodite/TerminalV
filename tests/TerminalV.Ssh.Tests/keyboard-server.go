// Isolated SSH server for the optional keyboard-interactive integration check.
// Build from tools/tsnet-bridge so its existing Go module supplies x/crypto/ssh.
package main

import (
	"crypto/rand"
	"crypto/rsa"
	"fmt"
	"io"
	"net"
	"os"

	"golang.org/x/crypto/ssh"
)

func main() {
	key, err := rsa.GenerateKey(rand.Reader, 2048)
	if err != nil { panic(err) }
	signer, err := ssh.NewSignerFromKey(key)
	if err != nil { panic(err) }
	config := &ssh.ServerConfig{KeyboardInteractiveCallback: func(c ssh.ConnMetadata, challenge ssh.KeyboardInteractiveChallenge) (*ssh.Permissions, error) {
		answers, err := challenge(c.User(), "", []string{"Password:"}, []bool{false})
		if err != nil { return nil, err }
		if c.User() != "test" || len(answers) != 1 || answers[0] != "test-password" { return nil, fmt.Errorf("authentication rejected") }
		return nil, nil
	}}
	config.AddHostKey(signer)
	listener, err := net.Listen("tcp", "127.0.0.1:0")
	if err != nil { panic(err) }
	defer listener.Close()
	if err := os.WriteFile(os.Args[1], []byte(fmt.Sprint(listener.Addr().(*net.TCPAddr).Port)), 0600); err != nil { panic(err) }
	for {
		conn, err := listener.Accept()
		if err != nil { return }
		go func() {
			server, channels, requests, err := ssh.NewServerConn(conn, config)
			if err != nil { conn.Close(); return }
			defer server.Close()
			go ssh.DiscardRequests(requests)
			for incoming := range channels {
				if incoming.ChannelType() != "session" { incoming.Reject(ssh.UnknownChannelType, "session only"); continue }
				channel, requests, err := incoming.Accept()
				if err != nil { continue }
				go func() {
					defer channel.Close()
					for request := range requests {
						ok := request.Type == "pty-req" || request.Type == "shell"
						if request.Type == "window-change" {
							var size struct { Columns, Rows, Width, Height uint32 }
							ok = ssh.Unmarshal(request.Payload, &size) == nil
							if ok { fmt.Fprintf(channel, "\r\nTV_WINDOW %d %d %d %d\r\n", size.Columns, size.Rows, size.Width, size.Height) }
						}
						if request.WantReply { request.Reply(ok, nil) }
						if request.Type == "shell" { go io.Copy(channel, channel) }
					}
				}()
			}
		}()
	}
}
