"use strict";

const fs = require("fs");
const net = require("net");
const { execFile } = require("child_process");

function loadConfig() {
  return fs.readFileSync("/etc/app/config.json", "utf8");
}

function saveCache(data) {
  fs.writeFileSync("%PackageRoot%/cache/last.json", data);
}

function loadUser(user) {
  return fs.readFileSync(`/etc/app/users/${user}.json`, "utf8");
}

function writeLog(path, entry) {
  fs.appendFileSync(path, entry);
}

function runGitStatus() {
  execFile("git", ["status"]);
}

function fetchStatus() {
  return fetch("https://api.example.com/status");
}

function startListener() {
  const server = net.createServer(() => {});
  server.listen(8080);
}

function clearCache() {
  fs.unlinkSync("%PackageRoot%/cache/last.json");
}

module.exports = { loadConfig, loadUser, runGitStatus };
