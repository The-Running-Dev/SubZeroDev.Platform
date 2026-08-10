/** Asks the OS for a free loopback TCP port by binding to port 0 and reading back what it chose. */
import { createServer } from "node:net";

export function freePort(): Promise<number> {
  return new Promise((resolve, reject) => {
    const server = createServer();
    server.listen(0, "127.0.0.1", () => {
      const address = server.address();
      if (address === null || typeof address === "string") {
        server.close();
        reject(new Error("could not allocate a free port"));
        return;
      }
      const port = address.port;
      server.close((closeError) => (closeError ? reject(closeError) : resolve(port)));
    });
  });
}
