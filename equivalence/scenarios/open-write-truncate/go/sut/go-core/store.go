package core

import "os"

func SaveCache(data []byte) error {
	return os.WriteFile("/var/lib/app/cache.json", data, 0o644)
}
