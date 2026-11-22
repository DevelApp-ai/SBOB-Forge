import * as https from "https";

export function fetchStatus(): void {
  https.get("https://api.example.com/status");
}
