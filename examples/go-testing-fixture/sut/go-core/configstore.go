package core

import (
	"net"
	"net/http"
	"os"
	"os/exec"
)

func LoadConfig() ([]byte, error) {
	return os.ReadFile("/etc/app/config.json")
}

func SaveCache(data []byte) error {
	return os.WriteFile("%PackageRoot%/cache/last.json", data, 0o644)
}

func LoadUser(user string) ([]byte, error) {
	return os.ReadFile("/etc/app/users/" + user + ".json")
}

func AppendLog(line string) error {
	f, err := os.OpenFile("/var/log/go-app.log", os.O_WRONLY|os.O_CREATE|os.O_APPEND, 0o644)
	if err != nil {
		return err
	}
	defer f.Close()
	_, err = f.WriteString(line)
	return err
}

func RunGitStatus() error {
	return exec.Command("git", "status").Run()
}

func FetchStatus() error {
	resp, err := http.Get("https://api.example.com/status")
	if err != nil {
		return err
	}
	defer resp.Body.Close()
	return nil
}

func StartListener() error {
	ln, err := net.Listen("tcp", ":8080")
	if err != nil {
		return err
	}
	return ln.Close()
}

func CleanCache() error {
	return os.Remove("%PackageRoot%/cache/last.json")
}
