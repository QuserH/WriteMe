import { IndexeddbPersistence } from "y-indexeddb";
import { api, fromBase64, toBase64, uuid, type Peer, type SharedDocData, type WireMessage, type Role } from "./api";
import { localOrigin, WebReplica } from "./replica";

export class LiveDocument {
  readonly replica: WebReplica;
  readonly persistence: IndexeddbPersistence;
  readonly commentDrafts = new Map<string, string>();
  private readonly generationKey: string;
  role: Role;
  status = "正在连接…";
  peers: Peer[] = [];
  error: string | null = null;
  private socket?: WebSocket;
  private serverVector?: Uint8Array;
  private timer?: ReturnType<typeof setTimeout>;
  private retry?: ReturnType<typeof setTimeout>;
  private heartbeat?: ReturnType<typeof setInterval>;
  private disposed = false;
  private revision = 0;
  private sequence = 0;
  private inFlight = new Map<string, number>();
  private listeners = new Set<() => void>();
  private composing = false;
  private deferred: Uint8Array[] = [];
  constructor(readonly data: SharedDocData, accountId: string) {
    this.role = data.role; this.replica = new WebReplica(fromBase64(data.state));
    const cache = `writeme-shared:${location.origin}:${accountId}:${data.document.id}`;
    this.generationKey = `${cache}:generation`;
    this.persistence = new IndexeddbPersistence(`${cache}${localStorage.getItem(this.generationKey) ?? ""}`, this.replica.doc);
    this.replica.doc.on("update", (_update: Uint8Array, origin: unknown) => {
      if (origin === localOrigin || origin === this.replica.undo) { this.revision++; this.status = this.socket?.readyState === WebSocket.OPEN ? "正在保存…" : "离线 · 修改保存在此浏览器"; this.schedule(); }
      this.emit();
    });
    void this.persistence.whenSynced.then(() => { if (!this.disposed) this.connect(); });
  }
  get canWrite() { return this.role !== "viewer" && !this.error; }
  subscribe(listener: () => void) { this.listeners.add(listener); return () => { this.listeners.delete(listener); }; }
  private emit() { for (const listener of this.listeners) listener(); }
  private connect() {
    if (this.disposed || this.error) return;
    this.status = "正在连接…"; this.emit();
    const socket = this.socket = new WebSocket(`${location.protocol === "https:" ? "wss:" : "ws:"}//${location.host}/api/shared/${this.data.document.id}/connect`);
    socket.onopen = () => { this.send({ type: "hello", vector: toBase64(this.replica.vector()) }); this.heartbeat = setInterval(() => this.send({ type: "ping" }), 20000); };
    socket.onmessage = event => {
      try {
        const message = JSON.parse(String(event.data)) as WireMessage;
        if (message.type === "sync") {
          if (message.update) this.apply(fromBase64(message.update)); this.serverVector = fromBase64(message.vector!);
          this.inFlight.clear(); if (this.canWrite) this.flush(); else this.status = "已连接 · 仅阅读";
        } else if (message.type === "update") { if (message.update) this.apply(fromBase64(message.update)); }
        else if (message.type === "ack") {
          this.serverVector = fromBase64(message.vector!); const revision = this.inFlight.get(message.id!); this.inFlight.delete(message.id!);
          if (revision === this.revision) this.status = "所有更改已保存";
        } else if (message.type === "peers") this.peers = message.peers ?? [];
        else if (message.type === "permissions") void api<SharedDocData>(`shared/${this.data.document.id}`).then(data => { this.role = data.role; if (!this.canWrite) this.status = "已连接 · 仅阅读"; this.emit(); }).catch(error => this.fail(error.message));
        else if (message.type === "error") this.fail(message.error ?? "无法保存这次更改");
        this.emit();
      } catch (error) { this.fail(error instanceof Error ? error.message : "无法读取共享内容"); }
    };
    socket.onclose = () => { clearInterval(this.heartbeat); this.serverVector = undefined; this.peers = []; if (!this.disposed && !this.error) { this.status = "离线 · 修改保存在此浏览器"; this.emit(); this.retry = setTimeout(() => this.connect(), 2500); } };
    socket.onerror = () => { this.status = "连接中断 · 正在重连"; this.emit(); };
  }
  private fail(message: string) { this.error = message; this.status = "尚未保存到服务器 · 本地草稿已保留"; this.socket?.close(); this.emit(); }
  private send(message: WireMessage) { if (this.socket?.readyState === WebSocket.OPEN) this.socket.send(JSON.stringify(message)); }
  private schedule() { clearTimeout(this.timer); this.timer = setTimeout(() => this.flush(), 100); }
  flush() {
    clearTimeout(this.timer); if (!this.serverVector || !this.canWrite || this.socket?.readyState !== WebSocket.OPEN) return;
    const id = String(++this.sequence); this.inFlight.set(id, this.revision); this.status = "正在保存…";
    this.send({ type: "update", id, update: toBase64(this.replica.difference(this.serverVector)), vector: toBase64(this.replica.vector()) }); this.emit();
  }
  presence(blockId: string | null) { this.send({ type: "presence", blockId }); }
  setComposing(value: boolean) {
    this.composing = value;
    if (!value && !this.disposed) {
      try { const updates = this.deferred.splice(0); for (const update of updates) this.replica.apply(update); }
      catch (error) { this.fail(error instanceof Error ? error.message : "无法合并远端内容，本地草稿已保留"); }
    }
  }
  useFreshCache() {
    // Keep the rejected database as a recovery copy; a fresh replica must never reapply that draft.
    localStorage.setItem(this.generationKey, `:recovery:${uuid()}`);
  }
  private apply(update: Uint8Array) { if (this.composing) this.deferred.push(update); else this.replica.apply(update); }
  dispose() { this.disposed = true; clearTimeout(this.timer); clearTimeout(this.retry); clearInterval(this.heartbeat); this.socket?.close(); void this.persistence.destroy(); this.replica.dispose(); this.listeners.clear(); }
}
