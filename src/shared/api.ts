export interface Avatar { color: string; text: string; imageVersion?: string | null }
export interface AvatarChange { color: string; text: string; imageData?: string | null; removeImage?: boolean }
export interface Profile { id: string; username: string; publicId: string | null; displayName: string; isAdmin: boolean; disabled: boolean; needsSetup: boolean; avatar?: Avatar | null; syncPaused?: boolean }
export type Role = "owner" | "editor" | "viewer";
export interface Workspace { id: string; name: string; role: Role; createdAt: number }
export interface Member { accountId: string; publicId: string; displayName: string; role: Role; avatar?: Avatar | null }
export interface SharedDocInfo { id: string; workspaceId: string; title: string; updatedAt: number; deleted: boolean }
export interface SharedDocData { document: SharedDocInfo; role: Role; state: string; protocol: number; syncPaused?: boolean }
export interface Peer { connectionId: string; accountId: string; displayName: string; publicId: string; color: string; blockId?: string | null; avatar?: Avatar | null }
export interface AccountUsage { profile: Profile; personalDocuments: number; conflicts: number; personalBytes: number; sharedDocuments: number; sharedBytes: number; workspaces: number; activeSessions: number; lastLoginAt: number | null; lastSyncAt: number | null; onlineConnections: number }
export interface AccountSession { id: string; device: string; createdAt: number | null; lastSeenAt: number | null; expiresAt: number; current: boolean }
export interface WorkspaceUsage { id: string; name: string; role: Role; documents: number; deletedDocuments: number; bytes: number; updatedAt: number | null }
export interface AccountData { usage: AccountUsage; workspaces: WorkspaceUsage[]; personalDocuments: Array<{ id: string; title: string; versions: number; bytes: number; deleted: boolean }>; sessions: AccountSession[]; assetCount: number; assetBytes: number }
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
export function avatarUrl(id: string | undefined | null, avatar?: Avatar | null): string | undefined {
  return id && avatar?.imageVersion && /^[A-Fa-f0-9]{64}$/.test(avatar.imageVersion) ? `/api/avatars/${encodeURIComponent(id)}/${avatar.imageVersion}` : undefined;
}
export function relativeTime(time: number | null | undefined): string {
  if (!time) return "暂无记录";
  const minutes = Math.max(0, Math.floor((Date.now() - time) / 60000));
  return minutes < 1 ? "刚刚" : minutes < 60 ? `${minutes} 分钟前` : minutes < 1440 ? `${Math.floor(minutes / 60)} 小时前` : new Date(time).toLocaleDateString("zh-CN");
}
export function fileSize(bytes: number): string { return bytes < 1024 ? `${bytes} B` : bytes < 1048576 ? `${(bytes / 1024).toFixed(1)} KB` : `${(bytes / 1048576).toFixed(1)} MB`; }
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
