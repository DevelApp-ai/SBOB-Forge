import * as fs from "fs";

export function loadConfig(): string {
  return fs.readFileSync("/etc/app/config.json", "utf8");
}
