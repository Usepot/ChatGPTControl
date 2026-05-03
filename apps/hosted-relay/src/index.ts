import { loadRelayConfig } from "./config.js";
import { startRelayServer } from "./server.js";

const config = loadRelayConfig();

startRelayServer(config)
  .then(() => {
    console.log(`Portable Codex relay listening on http://localhost:${config.port}`);
  })
  .catch((error) => {
    console.error("Failed to start relay", error);
    process.exitCode = 1;
  });
