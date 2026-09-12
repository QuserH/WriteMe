import { useCallback, useEffect, useRef, useState } from "react";
import type { Editor } from "@tiptap/core";
import Icon from "../components/Icon";
import SharedEditor, { BubbleIcon, paragraph as currentParagraph, type ParagraphTarget } from "./SharedEditor";
import { api, ApiError, roleName, type Member, type Profile, type Role, type SharedDocData, type SharedDocInfo, type Workspace } from "./api";
import { LiveDocument } from "./live";
import { Auth } from "./Auth";
import { Logo, UserAvatar, Field, Modal, ExitIcon } from "./ui";
import { ParagraphComments } from "./ParagraphComments";
import { ProfileSettings } from "./ProfileSettings";
import { useNarrowViewport, useSharedViewport } from "./viewport";
import "./shared.css";
import "./comments.css";

const displayTitle = (title: string) => title.trim() || "无标题";
export default function SharedApp() {
  useSharedViewport();
  const [profile, setProfile] = useState<Profile | null>(null); const [checking, setChecking] = useState(true); const [error, setError] = useState("");
  useEffect(() => {
    document.title = "WriteME · 一起把工作写好";
    void api<Profile>("me").then(setProfile).catch(error => { if (!(error instanceof ApiError && error.status === 401)) setError("暂时连接不上服务器，请检查网络后重试"); }).finally(() => setChecking(false));
  }, []);
  if (checking) return <div className="shared-app shared-loading"><Logo /><span>正在打开你的工作区…</span></div>;
  if (!profile || profile.needsSetup) return <Auth profile={profile} onLogin={setProfile} initialError={error} />;
  return <WorkspaceApp profile={profile} profileChanged={setProfile} logout={async () => { await api("logout", "POST"); setProfile(null); }} />;
}
function WorkspaceApp({ profile, profileChanged, logout }: { profile: Profile; profileChanged: (profile: Profile) => void; logout: () => Promise<void> }) {
  const [workspaces, setWorkspaces] = useState<Workspace[]>([]); const [workspace, setWorkspace] = useState<Workspace | null>(null);
  const [members, setMembers] = useState<Member[]>([]); const [documents, setDocuments] = useState<SharedDocInfo[]>([]);
  const [active, setActive] = useState<string | null>(null); const [live, setLive] = useState<LiveDocument | null>(null);
  const [revision, setRevision] = useState(0); const [query, setQuery] = useState(""); const [error, setError] = useState("");
  const [modal, setModal] = useState<"workspace" | "members" | "delete" | "recover" | "profile" | "actions" | null>(null);
  const [loadVersion, setLoadVersion] = useState(0); const [trash, setTrash] = useState(false); const [comments, setComments] = useState(false);
  const [paragraph, setParagraph] = useState<ParagraphTarget | null>(null); const [loading, setLoading] = useState(false);
  const drawer = useNarrowViewport(900); const phone = useNarrowViewport();
  const [sidebar, setSidebar] = useState(() => !matchMedia("(max-width:900px)").matches);
  const editor = useRef<Editor | null>(null); const ready = useCallback((value: Editor) => { editor.current = value; }, []);
  const closeModal = useCallback(() => setModal(null), []);
  const run = useCallback(async (action: () => Promise<void>) => { try { setError(""); await action(); } catch (error) { setError(error instanceof Error ? error.message : "暂时无法完成操作"); } }, []);
  useEffect(() => {
    let gone = false;
    const refresh = async () => {
      try { const next = await api<Profile>("me"); if (!gone && next.id === profile.id && JSON.stringify(next) !== JSON.stringify(profile)) profileChanged(next); }
      catch { /* The active document keeps its draft and reports session failures. */ }
    };
    const visible = () => { if (document.visibilityState === "visible") void refresh(); };
    const timer = setInterval(visible, 30000); window.addEventListener("focus", visible); document.addEventListener("visibilitychange", visible);
    return () => { gone = true; clearInterval(timer); window.removeEventListener("focus", visible); document.removeEventListener("visibilitychange", visible); };
  }, [profile, profileChanged]);
  useEffect(() => { setSidebar(!drawer); }, [drawer]);
  useEffect(() => {
    if (!drawer || !sidebar) return;
    const close = (event: KeyboardEvent) => { if (event.key === "Escape" && !event.isComposing) setSidebar(false); };
    document.addEventListener("keydown", close); return () => document.removeEventListener("keydown", close);
  }, [drawer, sidebar]);
  useEffect(() => { void run(async () => {
    const all = await api<Workspace[]>("workspaces"); setWorkspaces(all);
    setWorkspace(all.find(item => item.id === localStorage.getItem("writeme-last-workspace")) ?? all[0] ?? null);
  }); }, [run]);
  const refreshMembers = useCallback(async () => {
    if (!workspace) { setMembers([]); return; }
    setMembers(await api<Member[]>("workspaces/" + workspace.id + "/members"));
  }, [workspace?.id]);
  useEffect(() => {
    let gone = false;
    if (!workspace) { setMembers([]); return; }
    const refresh = async () => { try { const all = await api<Member[]>("workspaces/" + workspace.id + "/members"); if (!gone) setMembers(all); } catch { /* The document connection reports lost access. */ } };
    void refresh(); const timer = setInterval(() => void refresh(), 30000); return () => { gone = true; clearInterval(timer); };
  }, [workspace?.id, profile]);
  useEffect(() => {
    if (!workspace) return; let gone = false; localStorage.setItem("writeme-last-workspace", workspace.id);
    const refresh = async () => { try { const docs = await api<SharedDocInfo[]>("workspaces/" + workspace.id + "/documents" + (trash ? "?trash=true" : "")); if (!gone) setDocuments(docs); } catch { /* Active editor retains unsaved work during outages. */ } };
    void refresh(); const timer = setInterval(() => void refresh(), 6000); return () => { gone = true; clearInterval(timer); };
  }, [workspace, trash]);
  useEffect(() => {
    let closed = false; let current: LiveDocument | undefined; let unsubscribe: (() => void) | undefined;
    setLive(null); setComments(false); setParagraph(null); editor.current = null; setLoading(!!active);
    if (!active) return;
    void run(async () => {
      const data = await api<SharedDocData>("shared/" + active); if (closed) return;
      current = new LiveDocument(data, profile.id); unsubscribe = current.subscribe(() => setRevision(value => value + 1)); setLive(current);
    }).finally(() => { if (!closed) setLoading(false); });
    return () => { closed = true; unsubscribe?.(); current?.dispose(); };
  }, [active, profile.id, run, loadVersion]);
  const closeDrawer = () => { if (drawer) setSidebar(false); };
  const selectWorkspace = (selected: Workspace) => { setWorkspace(selected); setActive(null); setTrash(false); setDocuments([]); closeDrawer(); };
  const openDocument = (id: string) => { setActive(id); closeDrawer(); };
  const createDocument = () => run(async () => {
    if (!workspace) return; const data = await api<SharedDocData>("workspaces/" + workspace.id + "/documents", "POST", { title: "" });
    setDocuments(items => [data.document, ...items]); openDocument(data.document.id); setTrash(false);
  });
  const restore = (id: string) => run(async () => { await api("shared/" + id + "/restore", "POST"); setDocuments(items => items.filter(item => item.id !== id)); });
  const title = live?.replica.title.toString() ?? "";
  const count = live?.replica.readThreads().reduce((sum, thread) => sum + thread.messages.filter(message => !message.deleted).length, 0) ?? 0;
  const commentOn = useCallback((target: ParagraphTarget | null) => { setParagraph(target); setComments(true); }, []);
  const peers = [...new Map((live?.peers ?? []).map(peer => [peer.accountId, peer])).values()];
  const shown = documents.filter(document => displayTitle(document.title).toLowerCase().includes(query.toLowerCase()));
  const writable = workspace?.role !== "viewer" && !(live ? live.data.syncPaused : profile.syncPaused);
  return <div className={"shared-app shared-shell " + (sidebar ? "sidebar-open" : "sidebar-closed")} data-revision={revision}>
    <header className="shared-topbar">
      <Logo small /><button className="shared-icon" aria-label={sidebar && drawer ? "收起工作区导航" : "显示工作区导航"} aria-expanded={sidebar} aria-controls="workspace-navigation" onClick={() => setSidebar(!sidebar)}><Icon name="panel" /></button>
      <div className="shared-breadcrumb">{active ? displayTitle(title) : trash ? "回收站" : workspace?.name ?? "我的工作台"}</div>
      <div className="shared-top-actions">
        {workspace && !phone && <button className="shared-button shared-share" onClick={() => setModal("members")}><PeopleIcon />共享</button>}
        {live && <>
          {!phone && <div className="shared-peers" aria-label={peers.length + " 人在线"}>{peers.slice(0, 5).map(peer => <span key={peer.accountId} title={peer.displayName + " · 在线"}><UserAvatar name={peer.displayName} color={peer.color} accountId={peer.accountId} avatar={peer.accountId === profile.id ? profile.avatar : peer.avatar} /></span>)}</div>}
          <button className={"shared-button shared-comments-toggle" + (comments ? " selected" : "")} aria-label="查看全部评论" aria-expanded={comments} onClick={() => { setParagraph(null); setComments(!comments); }}><BubbleIcon />{!phone && "评论"}{count > 0 && <span className="shared-badge">{count}</span>}</button>
          {!phone && <>
            <button className="shared-icon" aria-label="撤销" title="撤销" disabled={!live.canWrite || !live.replica.undo.canUndo()} onClick={() => live.replica.undo.undo()}>↶</button>
            <button className="shared-icon" aria-label="重做" title="重做" disabled={!live.canWrite || !live.replica.undo.canRedo()} onClick={() => live.replica.undo.redo()}>↷</button>
            <button className="shared-icon" aria-label="删除当前文档" title="移至回收站" disabled={!live.canWrite} onClick={() => setModal("delete")}><Icon name="trash" size={17} /></button>
          </>}
        </>}
        {phone && <button className="shared-icon shared-more-button" aria-label="文档与工作区操作" onClick={() => setModal("actions")}><span aria-hidden="true">•••</span></button>}
      </div>
    </header>
    {drawer && sidebar && <button className="shared-navigation-backdrop" aria-label="关闭工作区导航" onClick={() => setSidebar(false)} />}
    <aside id="workspace-navigation" className="shared-sidebar" aria-label="工作区导航" inert={!sidebar}>
      <div className="shared-space-picker"><span className="shared-space-square">{[...(workspace?.name ?? "工作")][0]}</span>
        <select aria-label="切换共享工作区" value={workspace?.id ?? ""} onChange={event => { const item = workspaces.find(item => item.id === event.target.value); if (item) selectWorkspace(item); }}><option value="" disabled>共享工作区</option>{workspaces.map(item => <option key={item.id} value={item.id}>{item.name}</option>)}</select>
        <button className="shared-icon" aria-label="创建工作区" title="创建工作区" onClick={() => setModal("workspace")}><Icon name="plus" size={16} /></button>
      </div>
      <div className="shared-search"><Icon name="search" size={16} /><input aria-label="搜索共享文档" placeholder="搜索文档" value={query} onChange={event => setQuery(event.target.value)} /><kbd>⌕</kbd></div>
      <button className={"shared-nav" + (!trash && !active ? " active" : "")} onClick={() => { setTrash(false); setActive(null); closeDrawer(); }}><Icon name="file" size={17} />所有文档<span>{trash ? "" : documents.length}</span></button>
      {workspace && <button className="shared-nav" onClick={() => setModal("members")}><PeopleIcon />工作区成员</button>}
      <div className="shared-list-label"><span>{trash ? "回收站" : "工作区文档"}</span>{writable && workspace && !trash && <button className="shared-icon" aria-label="新建文档" title="新建文档" onClick={() => void createDocument()}><Icon name="plus" size={16} /></button>}</div>
      <div className="shared-document-list">{shown.map(document => <div key={document.id} className={"shared-document-item" + (active === document.id ? " selected" : "")}>
        <button onClick={() => { if (!trash) openDocument(document.id); }} disabled={trash}><Icon name="file" size={16} /><span>{displayTitle(live?.data.document.id === document.id ? title : document.title)}</span></button>
        {trash && writable && <button className="shared-restore" onClick={() => void restore(document.id)}>恢复</button>}
      </div>)}{!shown.length && <div className="shared-list-empty">{query ? "没有匹配的文档" : trash ? "回收站是空的" : "从第一篇文档开始"}</div>}</div>
      <div className="shared-sidebar-bottom"><button className={"shared-nav" + (trash ? " active" : "")} onClick={() => { setActive(null); setTrash(!trash); closeDrawer(); }}><Icon name="trash" size={16} />回收站</button>
        <div className="shared-current-user"><button className="shared-profile-trigger" aria-label="个人设置" onClick={() => setModal("profile")}><UserAvatar name={profile.displayName} accountId={profile.id} avatar={profile.avatar} /><span><b>{profile.displayName}</b><small>@{profile.publicId}</small></span></button>
          <button className="shared-icon" title="退出登录" aria-label="退出登录" onClick={() => void run(logout)}><ExitIcon /></button>
        </div>
      </div>
    </aside>
    <div className="shared-main" inert={drawer && sidebar}>
      {error && <div className="shared-banner" role="alert"><span>{error}</span><button onClick={() => setError("")} aria-label="关闭提示"><Icon name="close" size={15} /></button></div>}
      {live ? <div className="shared-document-layout"><main className="shared-paper-scroll"><article className="shared-paper">
        <input className="shared-document-title" aria-label="共享文档标题" placeholder="无标题" maxLength={500} value={title} readOnly={!live.canWrite} onChange={event => live.replica.setTitle(event.target.value)} onCompositionStart={() => live.setComposing(true)} onCompositionEnd={() => setTimeout(() => live.setComposing(false), 0)} />
        {live.error && <div className="shared-error" role="alert">{live.error}<button className="shared-link" onClick={() => downloadDraft(live)}>导出本地草稿</button><button className="shared-link" onClick={() => setModal("recover")}>重新打开服务器版本</button></div>}
        <SharedEditor live={live} onComment={commentOn} onReady={ready} members={members} selectedParagraph={comments ? paragraph?.id : undefined} />
        <div className="shared-paper-footer"><div className="shared-document-meta"><span className={"shared-connection-dot" + (live.status.startsWith("所有") || live.status.startsWith("已连接") ? " online" : "")} /><span role="status">{live.status}</span>{live.role === "viewer" && <span className="shared-access-label">仅阅读</span>}</div><button onClick={() => commentOn(null)}><BubbleIcon />添加文档评论</button></div>
      </article></main></div>
      : <main className="shared-home">
        <div className="shared-home-intro"><span className="shared-eyebrow">{workspace?.name ?? "我的工作台"}</span><h1>{loading ? "正在打开文档…" : trash ? "回收站" : workspace ? "所有文档" : "你好，" + profile.displayName}</h1>
          <p>{trash ? "移除的文档会留在这里，可以随时恢复。" : workspace ? "你的文档，以及正在一起完成的工作。" : "创建一个工作区，或请伙伴用你的个人 ID 添加你。"}</p>
          {!trash && <button className="shared-primary" onClick={() => workspace ? void createDocument() : setModal("workspace")} disabled={!writable || loading}><Icon name="plus" size={18} />{workspace ? "新建文档" : "创建工作区"}</button>}
        </div>
        {!loading && workspace ? <div className="shared-document-cards">{shown.map(doc => trash ? <div className="shared-trash-card" key={doc.id}><Icon name="file" size={23} /><h3>{displayTitle(doc.title)}</h3><button className="shared-button" disabled={!writable} onClick={() => void restore(doc.id)}>恢复文档</button></div>
          : <button key={doc.id} onClick={() => openDocument(doc.id)}><Icon name="file" size={23} /><h3>{displayTitle(doc.title)}</h3><span>{new Date(doc.updatedAt).toLocaleDateString("zh-CN", { month: "long", day: "numeric" })}更新</span></button>)}</div>
          : !workspace && <div className="shared-id-card"><UserAvatar name={profile.displayName} accountId={profile.id} avatar={profile.avatar} /><div><b>{profile.displayName}</b><span>@{profile.publicId}</span></div><small>把这个 ID 告诉你的伙伴</small></div>}
      </main>}
    </div>
    {comments && live && <ParagraphComments key={live.data.document.id + ":" + (paragraph?.id ?? "document")} live={live} profile={profile} members={members} target={paragraph} close={() => setComments(false)} all={() => setParagraph(null)} />}
    {modal === "profile" && <ProfileSettings profile={profile} saved={profileChanged} close={closeModal} />}
    {modal === "actions" && <Modal title={active ? displayTitle(title) : workspace?.name ?? "我的工作台"} close={closeModal}><div className="shared-mobile-actions">
      {live && <><button disabled={!live.canWrite || !live.replica.undo.canUndo()} onClick={() => { live.replica.undo.undo(); closeModal(); }}><span>↶</span>撤销上一步</button><button disabled={!live.canWrite || !live.replica.undo.canRedo()} onClick={() => { live.replica.undo.redo(); closeModal(); }}><span>↷</span>重做</button>
        <button onClick={() => { commentOn(editor.current ? currentParagraph(editor.current) : null); closeModal(); }}><BubbleIcon />评论当前段落</button></>}
      {workspace && <button onClick={() => setModal("members")}><PeopleIcon />工作区成员</button>}
      <button aria-label="个人设置" onClick={() => setModal("profile")}><UserAvatar name={profile.displayName} accountId={profile.id} avatar={profile.avatar} />个人设置</button>
      {live && <button className="danger-text" disabled={!live.canWrite} onClick={() => setModal("delete")}><Icon name="trash" />移至回收站</button>}
    </div></Modal>}
    {modal === "workspace" && <Modal title="创建工作区" description="用项目、团队或一件正在做的事来命名。" close={closeModal}><form onSubmit={event => { event.preventDefault(); const name = new FormData(event.currentTarget).get("name"); void run(async () => { const created = await api<Workspace>("workspaces", "POST", { name }); setWorkspaces(items => [...items, created]); selectWorkspace(created); closeModal(); }); }}><Field label="工作区名称"><input name="name" placeholder="例如：产品设计小组" maxLength={80} required /></Field><button className="shared-primary" type="submit">创建工作区 →</button></form>{error && <p className="shared-error">{error}</p>}</Modal>}
    {modal === "members" && workspace && <Members workspace={workspace} close={closeModal} changed={refreshMembers} />}
    {modal === "recover" && live && <Modal title="重新打开服务器版本？" description="先下载当前草稿和评论作为副本，再读取服务器内容。浏览器中原有的草稿也会保留。" close={closeModal}><div className="shared-modal-actions"><button className="shared-button" onClick={closeModal}>取消</button><button className="shared-primary" onClick={() => { downloadDraft(live); void run(async () => { await api<SharedDocData>("shared/" + live.data.document.id); live.useFreshCache(); closeModal(); setLoadVersion(value => value + 1); }); }}>保留草稿并重新打开</button></div>{error && <p className="shared-error" role="alert">{error}</p>}</Modal>}
    {modal === "delete" && live && <Modal title="将文档移至回收站？" description={"「" + displayTitle(title) + "」会从所有成员的文档列表移除，你可以在回收站恢复。"} close={closeModal}><div className="shared-modal-actions"><button className="shared-button" onClick={closeModal}>取消</button><button className="shared-danger" onClick={() => void run(async () => { await api("shared/" + active, "DELETE"); setDocuments(items => items.filter(item => item.id !== active)); setActive(null); closeModal(); })}>移至回收站</button></div>{error && <p className="shared-error">{error}</p>}</Modal>}
  </div>;
}
function Members({ workspace, close, changed }: { workspace: Workspace; close: () => void; changed: () => Promise<void> }) {
  const [members, setMembers] = useState<Member[]>([]); const [error, setError] = useState(""); const [busy, setBusy] = useState(false);
  const refresh = useCallback(() => api<Member[]>("workspaces/" + workspace.id + "/members").then(setMembers), [workspace.id]);
  useEffect(() => { void refresh().catch(error => setError(error.message)); }, [refresh]);
  const change = async (action: () => Promise<unknown>) => { setBusy(true); setError(""); try { await action(); await refresh(); await changed(); } catch (error) { setError((error as Error).message); } finally { setBusy(false); } };
  return <Modal title="工作区成员" description={workspace.name + " · " + members.length + " 位成员"} close={close}>
    {workspace.role === "owner" && <form className="shared-invite" onSubmit={event => { event.preventDefault(); const form = event.currentTarget; const data = new FormData(form); void change(async () => { await api("workspaces/" + workspace.id + "/members", "PUT", { publicId: data.get("id"), role: data.get("role") }); form.reset(); }); }}>
      <Field label="通过个人 ID 添加成员"><input name="id" placeholder="@ 对方的个人 ID" required maxLength={33} /></Field><div className="shared-form-row"><select name="role" aria-label="新成员权限"><option value="editor">可编辑和评论</option><option value="viewer">仅阅读</option><option value="owner">所有者</option></select><button className="shared-primary" disabled={busy}>添加成员</button></div>
    </form>}
    <div className="shared-members">{members.map(member => <div key={member.accountId} className="shared-member"><UserAvatar name={member.displayName} accountId={member.accountId} avatar={member.avatar} /><div><b>{member.displayName}</b><small>@{member.publicId}</small></div>
      {workspace.role === "owner" ? <><select aria-label={member.displayName + "的权限"} disabled={busy} value={member.role} onChange={event => void change(() => api("workspaces/" + workspace.id + "/members", "PUT", { publicId: member.publicId, role: event.target.value }))}>{(["owner", "editor", "viewer"] as Role[]).map(role => <option key={role} value={role}>{roleName(role)}</option>)}</select><button className="shared-icon" title="移除成员" aria-label={"移除" + member.displayName} disabled={busy} onClick={() => void change(() => api("workspaces/" + workspace.id + "/members/" + member.accountId, "DELETE"))}><Icon name="close" size={15} /></button></> : <small>{roleName(member.role)}</small>}
    </div>)}</div>{error && <p className="shared-error" role="alert">{error}</p>}
  </Modal>;
}
function downloadDraft(live: LiveDocument) {
  const url = URL.createObjectURL(new Blob([JSON.stringify(live.replica.readRoot(), null, 2)], { type: "application/json" }));
  const a = document.createElement("a"); a.href = url; a.download = displayTitle(live.replica.title.toString()) + "-草稿.json"; a.click(); setTimeout(() => URL.revokeObjectURL(url), 1000);
}
function PeopleIcon() { return <svg width="18" height="18" viewBox="0 0 20 20" fill="none" stroke="currentColor" strokeWidth="1.4" aria-hidden="true"><circle cx="8" cy="6" r="3" /><path d="M2 17v-2c0-5 12-5 12 0v2M13 3a3 3 0 0 1 0 6M15 11c3 0 3 3 3 6" /></svg>; }
