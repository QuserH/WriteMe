import { useEffect, useRef, useState, type ReactNode } from "react";
import { createPortal } from "react-dom";
import Icon from "../components/Icon";
import { avatarUrl, type Avatar } from "./api";

export function Logo({ small = false }: { small?: boolean }) { return <div className={`shared-brand${small ? " small" : ""}`}><span className="shared-mark">w<span>·</span></span><b>WriteME</b></div>; }
export function UserAvatar({ name, color, accountId, avatar, image }: { name: string; color?: string; accountId?: string | null; avatar?: Avatar | null; image?: string }) {
  const source = image ?? avatarUrl(accountId, avatar); const [failed, setFailed] = useState(false);
  useEffect(() => setFailed(false), [source]); const ink = avatar?.color ?? color ?? "#6C82AD";
  return <span className="shared-avatar" style={{ background: `${ink}18`, color: ink }}>{source && !failed ? <img src={source} alt={`${name}的头像`} onError={() => setFailed(true)} /> : avatar?.text || [...name][0] || "W"}</span>;
}
export function Field({ label, children, hint }: { label: string; children: ReactNode; hint?: string }) { return <div className="shared-field"><label><span>{label}</span>{children}</label>{hint && <small>{hint}</small>}</div>; }
export function Modal({ title, description, children, close, className = "" }: { title: string; description?: string; children: ReactNode; close: () => void; className?: string }) {
  const ref = useRef<HTMLDivElement>(null);
  const closeRef = useRef(close); closeRef.current = close;
  useEffect(() => { const old = document.activeElement as HTMLElement | null; ref.current?.focus({ preventScroll: true }); const key = (event: KeyboardEvent) => { if (event.key === "Escape" && !event.isComposing) { event.stopPropagation(); closeRef.current(); } if (event.key === "Tab") { const focusable = Array.from(ref.current?.querySelectorAll<HTMLElement>("button:not(:disabled),input:not(:disabled),select:not(:disabled),textarea:not(:disabled),a[href]") ?? []).filter(element => element.getClientRects().length); const first = focusable[0]; const last = focusable[focusable.length - 1]; if (event.shiftKey && (document.activeElement === first || document.activeElement === ref.current)) { event.preventDefault(); last?.focus(); } else if (!event.shiftKey && document.activeElement === last) { event.preventDefault(); first?.focus(); } } }; document.addEventListener("keydown", key); return () => { document.removeEventListener("keydown", key); if (old?.isConnected) old.focus({ preventScroll: true }); }; }, []);
  return createPortal(<div className="shared-app shared-modal-backdrop" onPointerDown={event => { if (event.target === event.currentTarget) close(); }}><div className={"shared-modal " + className} role="dialog" aria-modal="true" aria-label={title} ref={ref} tabIndex={-1}><button className="shared-icon modal-close" aria-label="关闭" onClick={close}><Icon name="close" /></button><h2>{title}</h2>{description && <p className="shared-muted">{description}</p>}{children}</div></div>, document.body);
}
export function AdminIcon() { return <svg width="18" height="18" viewBox="0 0 20 20" fill="none" stroke="currentColor" strokeWidth="1.4"><path d="m10 2 7 3v5c0 4-7 8-7 8s-7-4-7-8V5l7-3Z" /><path d="m6 10 3 3 5-6" /></svg>; }
export function ExitIcon() { return <svg width="18" height="18" viewBox="0 0 20 20" fill="none" stroke="currentColor" strokeWidth="1.4"><path d="M8 3H3v14h5M8 10h10m-4-4 4 4-4 4" /></svg>; }
