"use strict";

const net = require("net");

function startListener() {
  const server = net.createServer(() => {});
  server.listen(8080);
}

module.exports = { startListener };
