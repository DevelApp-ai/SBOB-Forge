import * as net from "net";

export function startListener(): void {
  const server = net.createServer(() => {});
  server.listen(8080);
}
