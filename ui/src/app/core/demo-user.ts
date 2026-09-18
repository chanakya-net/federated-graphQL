/** One element of the `/tokens.json` array (contracts/tokens.json.md). */
export interface DemoUser {
  sub: string;
  name: string;
  tenantId: string;
  /** Values from `patch`, `vulnerability`, `softwareinstall`; may be empty. Display only. */
  services: string[];
  /** Sent as `Authorization: Bearer <token>` on `/graphql` requests. */
  token: string;
}
