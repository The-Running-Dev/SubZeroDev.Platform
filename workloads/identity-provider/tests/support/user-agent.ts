/**
 * A cookie-keeping user agent for walking Keycloak's server-rendered sign-in pages without a
 * browser engine. It follows redirects inside Keycloak and stops at the first one that leaves it,
 * which is where an authorization code arrives. Port of the spike's helper onto fetch.
 */

export interface Answer {
  status: number;
  body: string;
  location: string | undefined;
}

const FORM = { "content-type": "application/x-www-form-urlencoded" };

export class UserAgent {
  private readonly jar = new Map<string, string>();
  readonly keycloak: string;

  constructor(keycloak: string) {
    this.keycloak = keycloak;
  }

  async go(url: string, init: { method?: string; headers?: Record<string, string>; body?: string } = {}): Promise<Answer> {
    const cookie = [...this.jar].map(([k, v]) => `${k}=${v}`).join("; ");
    const response = await fetch(url.replaceAll("&amp;", "&"), {
      method: init.method ?? "GET",
      redirect: "manual",
      headers: { ...init.headers, ...(cookie ? { cookie } : {}) },
      ...(init.body === undefined ? {} : { body: init.body }),
    });
    for (const c of response.headers.getSetCookie()) {
      const pair = c.split(";")[0]!;
      const i = pair.indexOf("=");
      const name = pair.slice(0, i);
      const value = pair.slice(i + 1);
      if (value === "" || /max-age=0/i.test(c)) this.jar.delete(name);
      else this.jar.set(name, value);
    }
    const location = response.headers.get("location") ?? undefined;
    return { status: response.status, body: await response.text(), location: location ? new URL(location, url).href : undefined };
  }

  post(url: string, fields: Record<string, string>): Promise<Answer> {
    return this.go(url, { method: "POST", headers: FORM, body: new URLSearchParams(fields).toString() });
  }

  /** Follows redirects that stay inside Keycloak; answers the page, or the redirect that leaves. */
  async follow(r: Answer): Promise<Answer> {
    for (let i = 0; i < 10 && (r.status === 302 || r.status === 303) && r.location?.startsWith(this.keycloak); i++) r = await this.go(r.location);
    return r;
  }

  async open(url: string): Promise<Answer> {
    return this.follow(await this.go(url));
  }

  async submit(url: string, fields: Record<string, string>): Promise<Answer> {
    return this.follow(await this.post(url, fields));
  }
}

// ---------------------------------------------------------------- reading Keycloak's pages

export const pageId = (a: Answer): string => a.body.match(/data-page-id="([^"]*)"/)?.[1] ?? `(status ${a.status}${a.location ? ` -> ${a.location}` : ""})`;

/** The visible error or notice on a page, if any. */
export const feedback = (a: Answer): string | undefined => {
  const flat = a.body.replace(/\s+/g, " ");
  return flat.match(/(?:kc-feedback-text|input-error[^"]*|pf-v5-c-alert__title|pf-m-error[^"]*)"[^>]*>\s*(?:<[^>]*>\s*)*([^<]{3,200})/)?.[1]?.trim();
};

export const authenticateAction = (a: Answer): string | undefined => a.body.match(/action="([^"]*login-actions\/authenticate[^"]*)"/)?.[1];
export const formById = (a: Answer, id: string): string | undefined =>
  a.body.match(new RegExp(`<form[^>]*id="${id}"[^>]*action="([^"]*)"`))?.[1] ?? a.body.match(new RegExp(`<form[^>]*action="([^"]*)"[^>]*id="${id}"`))?.[1];
/** A value from the inline WebAuthn script (`challenge : "…"`, `rpId : "…"`, `userid : "…"`). */
export const scriptField = (a: Answer, key: string): string | undefined => a.body.match(new RegExp(`${key}[ \\t]*:[ \\t]*"([^"]*)"`))?.[1];
export const hiddenFields = (a: Answer): Record<string, string> =>
  Object.fromEntries([...a.body.matchAll(/<input type="hidden" (?:id="[^"]*" )?name="([^"]*)" value="([^"]*)"/g)].map((m) => [m[1]!, m[2]!]));

/** The options on Keycloak 26.8.1's "select a credential" page: one form per option. */
export function credentialOptions(a: Answer): { id: string; label: string }[] {
  const parts = a.body.split(/<input type="hidden" name="authenticationExecution" value="/).slice(1);
  return parts.map((p) => ({
    id: p.slice(0, p.indexOf('"')),
    label: (p.match(/select-auth-box-headline[^>]*>([^<]*)/)?.[1] ?? "").replace(/\s+/g, " ").trim(),
  }));
}
