import * as fs from "fs";

export function saveCache(data: string): void {
  fs.writeFileSync("/var/lib/app/cache.json", data);
}
