/**
 * A browser's cookie handling, as far as this proof needs it: keep what each response's `Set-Cookie`
 * lines set, drop what they expire, and send the lot back. Names and values only — the proof talks to
 * one host, so `Domain`, `Path` and `Secure` do not select anything here. A deletion is a `Max-Age=0`
 * or an `Expires` in the past, which is how Adventures clears `sza_oauth_state`.
 */
export class CookieJar {
  private readonly cookies = new Map<string, string>();

  /** Applies every `Set-Cookie` a response carried, each as its own value. */
  absorb(setCookies: readonly string[]): void {
    for (const line of setCookies) {
      const [pair = "", ...attributes] = line.split(";").map((part) => part.trim());
      const separator = pair.indexOf("=");
      if (separator < 1) continue;
      const name = pair.slice(0, separator);
      const value = pair.slice(separator + 1);
      const expired = attributes.some((attribute) => {
        const at = attribute.indexOf("=");
        const key = (at < 0 ? attribute : attribute.slice(0, at)).toLowerCase();
        const given = at < 0 ? "" : attribute.slice(at + 1);
        if (key === "max-age") return Number(given) <= 0;
        if (key === "expires") return Date.parse(given) <= Date.now();
        return false;
      });
      if (expired) this.cookies.delete(name);
      else this.cookies.set(name, value);
    }
  }

  get(name: string): string | undefined {
    return this.cookies.get(name);
  }

  has(name: string): boolean {
    return this.cookies.has(name);
  }

  /** The `Cookie` request header, or `undefined` when the jar is empty. */
  header(): string | undefined {
    if (this.cookies.size === 0) return undefined;
    return [...this.cookies].map(([name, value]) => `${name}=${value}`).join("; ");
  }
}
