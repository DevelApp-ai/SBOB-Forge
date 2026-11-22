"use strict";

function fetchStatus() {
  return fetch("https://api.example.com/status");
}

module.exports = { fetchStatus };
