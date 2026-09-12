import { useCallback, useEffect, useRef, useState, type FormEvent } from "react";
import Icon from "../components/Icon";
import { api, ApiError, fileSize, relativeTime, roleName, type AccountData, type AccountUsage, type Profile } from "../shared/api";
import { Auth } from "../shared/Auth";
import { Logo, Field, Modal, UserAvatar, ExitIcon } from "../shared/ui";
import { ProfileSettings } from "../shared/ProfileSettings";
import { useSharedViewport } from "../shared/viewport";
import "../shared/shared.css";
import "./admin.css";

// Note: 独立后台展示服务器接收的数据，写入暂停与设备退出不删除用户资料 — 见 .agents/notes/implemented/architecture/2026-09-12-shared-workspaces-and-realtime.md
export default function AdminApp() {
  useSharedViewport();
  const [profile, setProfile] = useState<Profile | null>(null); const [checking, setChecking] = useState(true);
  const [error, setError] = useState(""); const [settings, setSettings] = useState(false);
  useEffect(() => {
    document.title = "WriteME · 管理控制台";
    void api<Profile>("me").then(setProfile).catch(error => { if (!(error instanceof ApiError && error.status === 401)) setError("暂时连接不上服务器，请稍后重试"); }).finally(() => setChecking(false));
  }, []);
  const logout = async () => { try { await api("logout", "POST"); setProfile(null); } catch (error) { setError((error as Error).message); } };
  if (checking) return <div className="shared-app shared-loading"><Logo /><span>正在打开管理控制台…</span></div>;
  if (!profile || profile.needsSetup) return <Auth profile={profile} onLogin={setProfile} initialError={error} admin />;
  return <div className="shared-app admin-app">
    <header className="admin-header"><Logo small /><span className="admin-header-label">管理控制台</span>
      <div className="admin-header-actions"><a href="/team" className="shared-button">打开笔记 ↗</a><button className="admin-profile-button" aria-label="个人设置" onClick={() => setSettings(true)}><UserAvatar name={profile.displayName} accountId={profile.id} avatar={profile.avatar} /><span>{profile.displayName}</span></button><button className="shared-icon" aria-label="退出登录" title="退出登录" onClick={() => void logout()}><ExitIcon /></button></div>
    </header>
    {profile.isAdmin ? <AdminPanel profile={profile} profileChanged={setProfile} sessionLost={() => setProfile(null)} /> : <main className="admin-access"><h1>此账号没有管理员权限</h1><p>请使用管理员账号登录，或返回你的工作区。</p><a className="shared-primary" href="/team">打开笔记</a><button className="shared-link" onClick={() => void logout()}>切换账号</button></main>}
    {settings && <ProfileSettings profile={profile} saved={setProfile} close={() => setSettings(false)} />}
  </div>;
}
function AdminPanel({ profile, profileChanged, sessionLost }: { profile: Profile; profileChanged: (profile: Profile) => void; sessionLost: () => void }) {
  const [accounts, setAccounts] = useState<AccountUsage[]>([]); const [error, setError] = useState(""); const [notice, setNotice] = useState("");
  const [form, setForm] = useState<Profile | "new" | null>(null); const [detail, setDetail] = useState<string | null>(null);
  const [query, setQuery] = useState(""); const [filter, setFilter] = useState("all"); const [loading, setLoading] = useState(true); const [updated, setUpdated] = useState<number | null>(null);
  const request = useRef(0);
  const refresh = useCallback(async () => { const revision = ++request.current; const all = await api<AccountUsage[]>("admin/accounts/usage"); if (revision !== request.current) return; setAccounts(all); setUpdated(Date.now()); setError(""); }, []);
  useEffect(() => {
    let closed = false;
    const load = async () => { try { await refresh(); } catch (error) { if (!closed) setError((error as Error).message); } finally { if (!closed) setLoading(false); } };
    void load(); const timer = setInterval(() => { if (document.visibilityState === "visible") void load(); }, 15000);
    return () => { closed = true; request.current++; clearInterval(timer); };
  }, [refresh, profile]);
  const failed = (error: unknown) => { if (error instanceof ApiError && error.status === 401) sessionLost(); else setError((error as Error).message); };
  const saved = async (next: Profile, created = false) => {
    setForm(null); if (next.id === profile.id) profileChanged(next);
    setNotice(created ? "账号已创建，对方首次登录后可设置自己的名字、ID 和密码。" : "账号已更新，原有文档和工作区保持关联。"); await refresh();
  };
  const shown = accounts.filter(({ profile: account, conflicts }) => {
    const search = (account.displayName + " " + account.username + " " + (account.publicId ?? "")).toLowerCase().includes(query.trim().toLowerCase());
    return search && (filter === "all" || filter === "active" && !account.disabled && !account.syncPaused || filter === "paused" && account.syncPaused || filter === "disabled" && account.disabled || filter === "conflicts" && conflicts > 0);
  });
  return <main className="admin-main">
    <div className="admin-heading"><div><span className="shared-eyebrow">你的工作，井然有序</span><h1>账号与同步</h1><p>管理成员的访问权限，查看工作区、数据与登录设备。</p></div><button className="shared-primary" onClick={() => setForm("new")}><Icon name="plus" size={18} />创建账号</button></div>
    <div className="admin-stats" aria-label="服务器数据概况">
      <div><span>已开通账号</span><strong>{accounts.length}</strong><small>{accounts.filter(item => !item.profile.disabled).length} 个可登录</small></div>
      <div><span>在线编辑连接</span><strong>{accounts.reduce((sum, item) => sum + item.onlineConnections, 0)}</strong><small>{accounts.filter(item => item.onlineConnections > 0).length} 位成员正在连接</small></div>
      <div><span>个人同步文档</span><strong>{accounts.reduce((sum, item) => sum + item.personalDocuments, 0)}</strong><small>包含保留的删除记录</small></div>
      <div><span>待处理的同步冲突</span><strong>{accounts.reduce((sum, item) => sum + item.conflicts, 0)}</strong><small>在所属账号的原生端处理</small></div>
    </div>
    {error && <p className="shared-error" role="alert">{error}</p>}{notice && <div className="admin-notice" role="status"><span>{notice}</span><button className="shared-icon" aria-label="关闭操作提示" onClick={() => setNotice("")}><Icon name="close" size={15} /></button></div>}
    <section className="admin-account-section">
      <div className="admin-list-heading"><h2>所有账号 <span>{accounts.length}</span></h2><button className="shared-link" onClick={() => void refresh().catch(failed)}>刷新数据</button></div>
      <div className="admin-filters"><label className="admin-search"><Icon name="search" size={17} /><input aria-label="搜索账号" value={query} onChange={event => setQuery(event.target.value)} placeholder="搜索名字、账号或 ID" /></label>
        <select aria-label="筛选账号状态" value={filter} onChange={event => setFilter(event.target.value)}><option value="all">所有状态</option><option value="active">可正常写入</option><option value="paused">已暂停写入</option><option value="disabled">已停用</option><option value="conflicts">有同步冲突</option></select>
      </div>
      <table className="admin-account-table"><thead><tr><th>成员</th><th>文档与工作区</th><th>最近接收数据</th><th>状态</th><th>管理</th></tr></thead><tbody>{shown.map(usage => {
        const account = usage.profile;
        return <tr key={account.id} data-account-id={account.id}>
          <td><div className="admin-account-person"><UserAvatar name={account.displayName} accountId={account.id} avatar={account.avatar} /><div><b>{account.displayName}{account.id === profile.id && <em>你</em>}</b><span>{account.username} · {account.isAdmin ? "管理员" : "成员"}</span><small>{account.publicId ? "@" + account.publicId : "等待设置个人 ID"}</small></div></div></td>
          <td data-label="文档与工作区"><div className="admin-data-summary"><b>个人 {usage.personalDocuments} <i>·</i> 共享 {usage.sharedDocuments}</b><small>{usage.workspaces} 个工作区{usage.conflicts > 0 && <span className="admin-conflict"> · {usage.conflicts} 处冲突</span>}</small></div></td>
          <td data-label="最近接收"><div className="admin-data-summary"><b title={usage.lastSyncAt ? new Date(usage.lastSyncAt).toLocaleString("zh-CN") : ""}>{relativeTime(usage.lastSyncAt)}</b><small>{usage.onlineConnections ? usage.onlineConnections + " 个编辑连接在线" : "当前无编辑连接"}</small></div></td>
          <td data-label="状态"><AccountState profile={account} /></td>
          <td><div className="admin-row-actions"><button onClick={() => setDetail(account.id)}>同步详情</button><button onClick={() => setForm(account)}>编辑账号</button></div></td>
        </tr>;
      })}</tbody></table>
      {!shown.length && <div className="admin-empty">{loading ? "正在读取账号与同步数据…" : "没有符合条件的账号"}</div>}
      <div className="admin-list-foot"><span>共享文档数表示该账号可以访问的文档。</span><span>{updated ? "更新于 " + new Date(updated).toLocaleTimeString("zh-CN", { hour: "2-digit", minute: "2-digit" }) : "等待更新"} · 每 15 秒刷新</span></div>
    </section>
    <p className="admin-data-explanation">接收时间表示服务器最近成功收到或交换数据的时间。尚未联网的设备可能仍有未上传的修改。</p>
    {form && <AccountForm account={form} self={profile.id} saved={saved} close={() => setForm(null)} />}
    {detail && <AccountDetail id={detail} self={profile.id} changed={async next => { if (next.id === profile.id) profileChanged(next); await refresh(); }} close={() => setDetail(null)} edit={account => { setDetail(null); setForm(account); }} />}
  </main>;
}
function AccountState({ profile }: { profile: Profile }) {
  return <span className={"admin-state " + (profile.disabled ? "disabled" : profile.syncPaused ? "paused" : profile.needsSetup ? "pending" : "")}>{profile.disabled ? "已停用" : profile.syncPaused ? "已暂停写入" : profile.needsSetup ? "待首次设置" : "正常"}</span>;
}
function AccountForm({ account, self, saved, close }: { account: Profile | "new"; self: string; saved: (profile: Profile, created?: boolean) => Promise<void>; close: () => void }) {
  const [busy, setBusy] = useState(false); const [error, setError] = useState(""); const creating = account === "new"; const own = !creating && account.id === self;
  const submit = async (event: FormEvent<HTMLFormElement>) => {
    event.preventDefault(); const data = new FormData(event.currentTarget); setBusy(true); setError("");
    try {
      const password = String(data.get("password") ?? "");
      const input = { username: String(data.get("username")), password: password || undefined, isAdmin: own || data.get("admin") === "on", ...(!creating ? { displayName: String(data.get("displayName")) } : {}) };
      const next = await api<Profile>(creating ? "admin/accounts" : "admin/accounts/" + account.id, creating ? "POST" : "PATCH", input);
      await saved(next, creating);
    } catch (error) { setError((error as Error).message); } finally { setBusy(false); }
  };
  return <Modal title={creating ? "为伙伴开通账号" : own ? "编辑我的管理员账号" : "编辑账号"} description={creating ? "设置一个登录账号和临时密码，对方首次登录时完成个人资料。" : own ? "修改登录名或密码后，当前页面保持登录，文档和工作区继续属于同一个账号。" : "修改名字或登录账号会保留原有数据。设置临时密码后，对方需要重新登录并更新密码。"} close={close}>
    <form onSubmit={event => void submit(event)}>
      <Field label="登录账号"><input name="username" required defaultValue={creating ? "" : account.username} maxLength={100} placeholder="例如：lin" autoComplete="off" /></Field>
      {!creating && <Field label="显示名字"><input name="displayName" required defaultValue={account.displayName} maxLength={40} /></Field>}
      <Field label={own ? "新密码" : "临时密码"} hint={creating ? "至少 6 个字符" : "至少 6 个字符，留空保留现有密码"}><input name="password" type="password" minLength={6} maxLength={1024} required={creating} autoComplete="new-password" /></Field>
      {!own && <label className="shared-checkbox"><input type="checkbox" name="admin" defaultChecked={!creating && account.isAdmin} />管理员权限</label>}
      {error && <p className="shared-error" role="alert">{error}</p>}
      <button className="shared-primary" disabled={busy}>{busy ? "正在保存…" : creating ? "创建账号" : "保存账号"}</button>
    </form>
  </Modal>;
}
function AccountDetail({ id, self, changed, close, edit }: { id: string; self: string; changed: (profile: Profile) => Promise<void>; close: () => void; edit: (profile: Profile) => void }) {
  const [data, setData] = useState<AccountData | null>(null); const [tab, setTab] = useState("workspaces"); const [error, setError] = useState("");
  const [busy, setBusy] = useState(false); const [confirm, setConfirm] = useState<{ label: string; message: string; action: () => Promise<unknown> } | null>(null);
  const request = useRef(0);
  const refresh = useCallback(async () => { const revision = ++request.current; const next = await api<AccountData>("admin/accounts/" + id + "/data"); if (revision === request.current) setData(next); return next; }, [id]);
  useEffect(() => { void refresh().catch(error => setError(error.message)); const timer = setInterval(() => void refresh().catch(error => setError(error.message)), 15000); return () => { request.current++; clearInterval(timer); }; }, [refresh]);
  const run = async (action: () => Promise<unknown>) => {
    setBusy(true); setError(""); try { await action(); const next = await refresh(); await changed(next.usage.profile); setConfirm(null); } catch (error) { setError((error as Error).message); } finally { setBusy(false); }
  };
  const profile = data?.usage.profile;
  return <Modal title={profile ? profile.displayName + " · 同步详情" : "同步详情"} close={close} className="admin-detail-modal">
    {error && <p className="shared-error" role="alert">{error}</p>}
    {!data || !profile ? <p className="shared-muted">正在读取账号数据…</p> : <>
      <div className="admin-detail-person"><UserAvatar name={profile.displayName} accountId={profile.id} avatar={profile.avatar} /><div><b>{profile.username}</b><small>{profile.publicId ? "@" + profile.publicId : "个人 ID 尚未设置"}</small></div><AccountState profile={profile} /><button className="shared-link" onClick={() => edit(profile)}>编辑账号</button></div>
      <div className="admin-detail-stats"><div><span>个人同步状态</span><b>{fileSize(data.usage.personalBytes)}</b></div><div><span>可访问的共享状态</span><b>{fileSize(data.usage.sharedBytes)}</b></div><div><span>个人附件</span><b>{fileSize(data.assetBytes)}<small> · {data.assetCount} 个</small></b></div></div>
      <div className="admin-activity"><span>最近接收 <b>{relativeTime(data.usage.lastSyncAt)}</b></span><span>最近登录 <b>{relativeTime(data.usage.lastLoginAt)}</b></span></div>
      <div className="admin-detail-controls"><button className="shared-button" disabled={busy} onClick={() => profile.syncPaused ? void run(() => api("admin/accounts/" + id, "PATCH", { syncPaused: false })) : setConfirm({ label: "暂停写入", message: "暂停此账号向服务器写入数据？仍可阅读，已有数据和设备上的草稿会保留。", action: () => api("admin/accounts/" + id, "PATCH", { syncPaused: true }) })}>{profile.syncPaused ? "恢复同步写入" : "暂停同步写入"}</button>
        {id !== self && <button className="shared-button" disabled={busy} onClick={() => profile.disabled ? void run(() => api("admin/accounts/" + id, "PATCH", { disabled: false })) : setConfirm({ label: "停用账号", message: "停用后，此账号的登录和编辑连接会退出。账号的数据会保留，稍后可以重新启用。", action: () => api("admin/accounts/" + id, "PATCH", { disabled: true }) })}>{profile.disabled ? "重新启用账号" : "停用账号"}</button>}
      </div>
      {confirm && <div className="admin-confirm"><p>{confirm.message}</p><div><button className="shared-danger" disabled={busy} onClick={() => void run(confirm.action)}>{confirm.label}</button><button className="shared-link" disabled={busy} onClick={() => setConfirm(null)}>取消</button></div></div>}
      <div className="admin-detail-tabs" role="tablist" aria-label="账号数据分类">{[["workspaces", "共享工作区", data.workspaces.length], ["personal", "个人文档", data.usage.personalDocuments], ["sessions", "登录设备", data.usage.activeSessions]].map(([value, label, count]) => <button key={value} role="tab" aria-selected={tab === value} onClick={() => setTab(String(value))}>{label}<span>{count}</span></button>)}</div>
      <div className="admin-detail-content">
        {tab === "workspaces" && <>{data.workspaces.map(workspace => <div className="admin-resource" key={workspace.id}><span className="admin-resource-icon"><Icon name="panel" /></span><div><b>{workspace.name}</b><p>{workspace.documents} 篇文档 · {workspace.deletedDocuments} 篇在回收站 · {roleName(workspace.role)}</p><small>最近更新 {relativeTime(workspace.updatedAt)}</small></div><span>{fileSize(workspace.bytes)}</span></div>)}{!data.workspaces.length && <p className="admin-empty">还没有加入共享工作区</p>}<p className="admin-detail-hint">这里显示该账号有权访问的共享数据。同一工作区会出现在每位成员的账号下。</p></>}
        {tab === "personal" && <>{data.personalDocuments.map(document => <div className="admin-resource" key={document.id}><span className="admin-resource-icon"><Icon name="file" /></span><div><b>{document.title || "无标题"}</b><p>{document.deleted ? "已删除的同步记录" : document.versions > 1 ? document.versions + " 个并发版本，需在原生端选择" : "已收到文档"} · {fileSize(document.bytes)}</p></div></div>)}{!data.personalDocuments.length && <p className="admin-empty">原生端尚未向此账号同步个人文档</p>}<p className="admin-detail-hint">显示最近 200 条个人文档记录。共享文档在“共享工作区”中查看。</p></>}
        {tab === "sessions" && <><div className="admin-devices-heading"><span>有效登录 · 最近活动</span><button className="shared-link" disabled={busy || !data.sessions.some(session => !session.current)} onClick={() => setConfirm({ label: id === self ? "退出其他设备" : "退出所有设备", message: id === self ? "退出其他设备的登录？当前管理页面会保持登录。" : "退出这个账号的所有登录设备？设备上的未发送草稿会保留，需要重新登录后继续。", action: () => api("admin/accounts/" + id + "/sessions/revoke", "POST") })}>{id === self ? "退出其他设备" : "退出所有设备"}</button></div>
          {data.sessions.map(session => <div className="admin-resource admin-device" key={session.id}><span className="admin-resource-icon"><DeviceIcon mobile={session.device.startsWith("Android") || session.device.startsWith("iOS")} /></span><div><b>{session.device}{session.current && <em>当前设备</em>}</b><p>{relativeTime(session.lastSeenAt)} 活动</p><small>{session.createdAt ? new Date(session.createdAt).toLocaleString("zh-CN") + " 登录" : "升级前的登录"}</small></div>{!session.current && <button className="shared-link" disabled={busy} onClick={() => setConfirm({ label: "退出该设备", message: "退出 " + session.device + "？这个设备下次访问时需要重新登录。", action: () => api("admin/accounts/" + id + "/sessions/" + session.id, "DELETE") })}>退出</button>}</div>)}
          {!data.sessions.length && <p className="admin-empty">没有仍有效的登录设备</p>}<p className="admin-detail-hint">登录有效不代表设备当前在线。编辑连接在线数：{data.usage.onlineConnections}。</p>
        </>}
      </div>
    </>}
  </Modal>;
}
function DeviceIcon({ mobile }: { mobile: boolean }) { return <svg width="20" height="20" viewBox="0 0 20 20" fill="none" stroke="currentColor" strokeWidth="1.3" aria-hidden="true">{mobile ? <><rect x="5" y="2" width="10" height="16" rx="2" /><path d="M8 15h4" /></> : <><rect x="2" y="3" width="16" height="11" rx="2" /><path d="M7 18h6M10 14v4" /></>}</svg>; }
