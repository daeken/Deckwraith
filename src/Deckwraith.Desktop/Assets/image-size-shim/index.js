"use strict";

function imageSize(_input, callback) {
  const error = new Error("Deckwraith does not enable Electron.NET splash images.");
  if (typeof callback === "function") {
    queueMicrotask(() => callback(error));
    return;
  }

  throw error;
}

module.exports = { imageSize };
