import { useCallback, useEffect, useState } from "react";
import Icon from "../components/Icon";
import { api, ApiError, type Profile } from "../shared/api";
import { Auth } from "../shared/Auth";
import { Logo, Field, Modal, UserAvatar, ExitIcon } from "../shared/ui";
import "../shared/shared.css";
import "./admin.css";

// Note: 管理后台独立于笔记界面，见 .agents/notes/implemented/architecture/2026-09-12-shared-workspaces-and-realtime.md
export default function AdminApp() {
  const [profile, setProfile] = useState<Profile | null>(null);
  const [checking, setChecking] = useState(true);
  const [error, setError] = useState("");
  useEffect(() => { document.title = "WriteME · 管理控制台"; void api<Profile>("me").then(setProfile).catch(error => { if (!(error instanceof ApiError && error.status === 401)) setError("暂时连接不上服务器，请稍后重试"); }).finally(() => setChecking(false)); }, []);
  const logout = async () => { try { await api("logout", "POST"); setProfile(null); } catch (error) { setError((error as Error).message); } };
  if (checking) return <div className="shared-app shared-loading"><Logo /><span>正在打开管理控制台…</span></div>;
  if (!profile || profile.needsSetup) return <Auth profile={profile} onLogin={setProfile} initialError={error} admin />;
  return <div className="shared-app admin-app"><header className="admin-header"><Logo small /><span className="admin-header-label">管理控制台</span><div className="admin-header-actions"><a href="/team" className="shared-button">打开笔记 ↗</a><UserAvatar name={profile.displayName} /><span>{profile.displayName}</span><button className="shared-icon" aria-label="退出登录" title="退出登录" onClick={() => void logout()}><ExitIcon /></button></div></header>
    {profile.isAdmin ? <AdminPanel profile={profile} sessionLost={() => setProfile(null)} /> : <main className="admin-access"><h1>此账号没有管理员权限</h1><p>请使用管理员账号登录，或返回你的工作区。</p><a className="shared-primary" href="/team">打开笔记</a><button className="shared-link" onClick={() => void logout()}>切换账号</button></main>}
  </div>;
}
function AdminPanel({ profile, sessionLost }: { profile: Profile; sessionLost: () => void }) {
  const [accounts, setAccounts] = useState<Profile[]>([]); const [error, setError] = useState(""); const [notice, setNotice] = useState(""); const [form, setForm] = useState<Profile | "new" | null>(null); const [busy, setBusy] = useState(false); const close = useCallback(() => setForm(null), []);
  const refresh = useCallback(() => api<Profile[]>("admin/accounts").then(setAccounts), []); useEffect(() => { void refresh().catch(error => setError(error.message)); }, [refresh]);
  const run = async (action: () => Promise<void>) => { setBusy(true); setError(""); try { await action(); await refresh(); } catch (error) { if (error instanceof ApiError && error.status === 401) sessionLost(); else setError((error as Error).message); } finally { setBusy(false); } };
  return <main className="shared-admin"><div className="shared-admin-heading"><div><span className="shared-eyebrow">管理控制台</span><h1>账号管理</h1><p>开通登录账号。伙伴首次登录后，设置自己的名字和 ID。</p></div><button className="shared-primary" onClick={() => setForm("new")}><Icon name="plus" size={18} />创建账号</button></div><div className="shared-admin-stats"><div><span>全部账号</span><b>{accounts.length}</b></div><div><span>已启用</span><b>{accounts.filter(account => !account.disabled).length}</b></div><div><span>待完成设置</span><b>{accounts.filter(account => account.needsSetup).length}</b></div></div>
    {error && <p className="shared-error" role="alert">{error}</p>}{notice && <p className="shared-success" role="status">{notice}</p>}
    <div className="shared-account-table"><div className="shared-table-heading"><h3>账号与访问</h3><span>共 {accounts.length} 个账号</span></div><table><thead><tr><th>用户</th><th>登录账号</th><th>角色</th><th>状态</th><th>操作</th></tr></thead><tbody>{accounts.map(account => <tr key={account.id}><td><div className="shared-member"><UserAvatar name={account.displayName} /><div><b>{account.displayName}</b><small>{account.publicId ? `@${account.publicId}` : "等待设置个人 ID"}</small></div></div></td><td>{account.username}</td><td>{account.isAdmin ? "管理员" : "成员"}</td><td><span className={`shared-state-pill ${account.disabled ? "disabled" : ""}`}>{account.disabled ? "已停用" : account.needsSetup ? "待首次设置" : "正常"}</span></td><td><div className="shared-row-actions"><button onClick={() => setForm(account)}>重置密码</button>{account.id !== profile.id && <button disabled={busy} onClick={() => void run(async () => { await api(`admin/accounts/${account.id}`, "PATCH", { disabled: !account.disabled }); setNotice(account.disabled ? "账号已重新启用" : "账号已停用，现有登录也已失效"); })}>{account.disabled ? "启用" : "停用"}</button>}</div></td></tr>)}</tbody></table></div>
    {form && <Modal title={form === "new" ? "为伙伴开通账号" : `重置 ${form.displayName} 的密码`} description={form === "new" ? "将登录账号和临时密码交给对方即可。" : "设置临时密码后，原有登录会失效。对方下次登录需要设置新密码。"} close={close}><form onSubmit={event => { event.preventDefault(); const data = new FormData(event.currentTarget); void run(async () => { if (form === "new") await api("admin/accounts", "POST", { username: data.get("username"), password: data.get("password"), isAdmin: data.get("admin") === "on" }); else await api(`admin/accounts/${form.id}`, "PATCH", { password: data.get("password") }); if (form !== "new" && form.id === profile.id) { sessionLost(); return; } setNotice(form === "new" ? "账号已创建，可以将登录信息交给伙伴。" : "临时密码已设置。对方下次登录需要更新密码。"); close(); }); }}>{form === "new" && <Field label="登录账号"><input name="username" required maxLength={100} placeholder="例如：lin" autoComplete="off" /></Field>}<Field label="临时密码" hint="至少 12 个字符"><input name="password" type="password" minLength={12} maxLength={1024} required autoComplete="new-password" /></Field>{form === "new" && <label className="shared-checkbox"><input type="checkbox" name="admin" />同时授予管理员权限</label>}<button className="shared-primary" disabled={busy}>{form === "new" ? "创建账号" : "设置临时密码"}</button>{error && <p className="shared-error" role="alert">{error}</p>}</form></Modal>}
  </main>;
}
