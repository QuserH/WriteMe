export interface Profile { id: string; username: string; publicId: string | null; displayName: string; isAdmin: boolean; disabled: boolean; needsSetup: boolean }
export type Role = "owner" | "editor" | "viewer";
export interface Workspace { id: string; name: string; role: Role; createdAt: number }
export interface Member { accountId: string; publicId: string; displayName: string; role: Role }
export interface SharedDocInfo { id: string; workspaceId: string; title: string; updatedAt: number; deleted: boolean }
export interface SharedDocData { document: SharedDocInfo; role: Role; state: string; protocol: number }
export interface Peer { connectionId: string; accountId: string; displayName: string; publicId: string; color: string; blockId?: string | null }
export interface Message { id: string; author: string; authorId: string | null; text: string; createdAt: number; editedAt?: number | null; replyTo?: string | null; deleted?: boolean }
export interface Thread { id: string; quote: string; anchored: boolean; wholeBlock: boolean; messages: Message[] }
export interface WireMessage { type: string; update?: string; vector?: string; id?: string; error?: string; peers?: Peer[]; blockId?: string | null }
export class ApiError extends Error { constructor(message: string, public status: number) { super(message); } }
export async function api<T>(path: string, method = "GET", body?: unknown): Promise<T> {
  const response = await fetch(`/api/${path}`, { method, credentials: "same-origin", headers: body === undefined ? undefined : { "Content-Type": "application/json" }, body: body === undefined ? undefined : JSON.stringify(body) });
  if (!response.ok) {
    const data = await response.json().catch(() => null) as { error?: string } | null;
    throw new ApiError(data?.error ?? (response.status === 401 ? "请重新登录" : "暂时无法完成，请稍后重试"), response.status);
  }
  return response.status === 204 ? undefined as T : response.json() as Promise<T>;
}
export const roleName = (role: Role) => ({ owner: "所有者", editor: "可编辑", viewer: "仅阅读" })[role];
export function uuid(): string {
  // randomUUID requires a secure context; getRandomValues also works on the explicitly supported LAN HTTP entry.
  const bytes = crypto.getRandomValues(new Uint8Array(16)); bytes[6] = (bytes[6] & 15) | 64; bytes[8] = (bytes[8] & 63) | 128;
  const hex = [...bytes].map(value => value.toString(16).padStart(2, "0")).join("");
  return `${hex.slice(0, 8)}-${hex.slice(8, 12)}-${hex.slice(12, 16)}-${hex.slice(16, 20)}-${hex.slice(20)}`;
}
export const fromBase64 = (value: string) => Uint8Array.from(atob(value), character => character.charCodeAt(0));
export function toBase64(value: Uint8Array): string {
  let binary = ""; for (let i = 0; i < value.length; i += 8192) binary += String.fromCharCode(...value.subarray(i, i + 8192)); return btoa(binary);
}
