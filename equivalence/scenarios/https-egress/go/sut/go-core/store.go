package core

import "net/http"

func FetchStatus() error {
	resp, err := http.Get("https://api.example.com/status")
	if err != nil {
		return err
	}
	defer resp.Body.Close()
	return nil
}
