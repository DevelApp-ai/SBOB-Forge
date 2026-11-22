package core

import "os"

func LoadConfig() ([]byte, error) {
	return os.ReadFile("/etc/app/config.json")
}
