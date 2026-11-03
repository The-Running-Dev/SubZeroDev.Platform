/**
 * A loopback TCP listener that counts the connections made to it and answers none. A configuration
 * that points the process under test at its address and then never shows a connection has not
 * contacted it; the control is a connection the test makes itself, which the count must show.
 */
import { createServer } from "node:net";
import type { Server, Socket } from "node:net";

export interface ConnectionCounter {
  readonly port: number;
  /** Connections accepted so far. */
  connections(): number;
  close(): Promise<void>;
}

export async function startConnectionCounter(): Promise<ConnectionCounter> {
  let accepted = 0;
  const open = new Set<Socket>();
  const server: Server = createServer((socket) => {
    accepted += 1;
    open.add(socket);
    socket.on("close", () => open.delete(socket));
    socket.on("error", () => undefined);
  });

  await new Promise<void>((resolve, reject) => {
    server.once("error", reject);
    server.listen(0, "127.0.0.1", () => resolve());
  });
  const address = server.address();
  if (address === null || typeof address === "string") throw new Error("connection counter has no TCP address");

  return {
    port: address.port,
    connections: () => accepted,
    close: () =>
      new Promise<void>((resolve) => {
        for (const socket of open) socket.destroy();
        server.close(() => resolve());
      }),
  };
}
