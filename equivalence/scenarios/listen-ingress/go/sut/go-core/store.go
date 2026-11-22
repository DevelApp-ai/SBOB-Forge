package core

import "net"

func StartListener() error {
	ln, err := net.Listen("tcp", ":8080")
	if err != nil {
		return err
	}
	return ln.Close()
}
