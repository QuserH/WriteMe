import { useEffect, useLayoutEffect, useRef, useState, type CSSProperties } from "react";
import { createPortal } from "react-dom";
import Icon from "../components/Icon";
import { BubbleIcon, type ParagraphTarget } from "./SharedEditor";
import { relativeTime, uuid, type Member, type Message, type Profile, type Thread } from "./api";
import type { CommentContext, LiveDocument } from "./live";
import { findNode, paragraphThreadIds } from "./paragraphDecorations";
import { localOrigin } from "./replica";
import { UserAvatar } from "./ui";
import { useNarrowViewport } from "./viewport";

// Note: 段落下方入口在手机打开底部评论弹层，草稿按原消息保留 — 见 .agents/notes/implemented/architecture/2026-09-12-shared-workspaces-and-realtime.md
export function ParagraphComments({ live, profile, members, target, close, all }: {
  live: LiveDocument; profile: Profile; members: Member[]; target: ParagraphTarget | null; close: () => void; all: () => void;
}) {
  const scope = live.data.document.id + ":" + (target?.id ?? "document");
  const [context, setContext] = useState<CommentContext | null>(() => live.commentTargets.get(scope) ?? null);
  const [error, setError] = useState(""); const [confirm, setConfirm] = useState<{ thread: Thread; message: Message } | null>(null);
  const [, redraw] = useState(0); const input = useRef<HTMLTextAreaElement>(null); const panel = useRef<HTMLElement>(null);
  const list = useRef<HTMLDivElement>(null); const composing = useRef(false); const followEnd = useRef(false);
  const narrow = useNarrowViewport(); const [position, setPosition] = useState<CSSProperties>({});
  const closeRef = useRef(close); closeRef.current = close;
  const key = scope + (context ? ":" + context.mode + ":" + context.message.id : "");
  const text = live.commentDrafts.get(key) ?? (context?.mode === "edit" ? context.message.text : "");
  const draft = (value: string) => { live.commentDrafts.set(key, value); redraw(value => value + 1); };
  const choose = (next: CommentContext | null) => {
    if (next) live.commentTargets.set(scope, next); else live.commentTargets.delete(scope);
    setContext(next); setError("");
  };
  const root = live.replica.readRoot(); const node = target ? findNode(root, target.id) : null;
  const ids = paragraphThreadIds(node); const threads = live.replica.readThreads().filter(thread => !target || ids.has(thread.id));
  const count = threads.reduce((total, thread) => total + thread.messages.filter(message => !message.deleted).length, 0);
  const own = (message: Message) => message.authorId === profile.id;
  useEffect(() => {
    if (narrow) return;
    const outside = (event: PointerEvent) => { if (event.target instanceof Node && !panel.current?.contains(event.target)) closeRef.current(); };
    document.addEventListener("pointerdown", outside, true);
    return () => document.removeEventListener("pointerdown", outside, true);
  }, [narrow]);
  useEffect(() => {
    const previous = document.activeElement as HTMLElement | null; panel.current?.focus({ preventScroll: true });
    const keydown = (event: KeyboardEvent) => {
      if (event.isComposing || composing.current) return;
      if (event.key === "Escape") { event.preventDefault(); event.stopPropagation(); closeRef.current(); }
      if (event.key !== "Tab") return;
      const items = Array.from(panel.current?.querySelectorAll<HTMLElement>("button:not(:disabled),textarea:not(:disabled),[tabindex='0']") ?? []).filter(item => item.getClientRects().length);
      const first = items[0]; const last = items.at(-1);
      if (event.shiftKey && (document.activeElement === first || document.activeElement === panel.current)) { event.preventDefault(); last?.focus(); }
      else if (!event.shiftKey && document.activeElement === last) { event.preventDefault(); first?.focus(); }
    };
    document.addEventListener("keydown", keydown, true);
    return () => { document.removeEventListener("keydown", keydown, true); if (previous?.isConnected && !narrow) previous.focus({ preventScroll: true }); };
  }, [narrow]);
  useLayoutEffect(() => {
    if (narrow) { setPosition({}); return; }
    const measure = () => {
      const anchor = target ? document.querySelector<HTMLElement>('[data-comment-block="' + CSS.escape(target.id) + '"]')?.getBoundingClientRect() : null;
      const height = panel.current?.offsetHeight ?? 440; const width = 364;
      const left = anchor ? Math.min(innerWidth - width - 20, anchor.right + 14) : innerWidth - width - 24;
      const top = anchor ? Math.min(innerHeight - height - 16, anchor.bottom + 8) : 62;
      setPosition({ left: Math.max(16, left), top: Math.max(16, top) });
    };
    measure(); const observer = new ResizeObserver(measure); if (panel.current) observer.observe(panel.current);
    window.addEventListener("resize", measure); window.addEventListener("scroll", measure, true);
    return () => { observer.disconnect(); window.removeEventListener("resize", measure); window.removeEventListener("scroll", measure, true); };
  }, [narrow, target?.id]);
  useLayoutEffect(() => {
    if (followEnd.current && list.current) { list.current.scrollTop = list.current.scrollHeight; followEnd.current = false; }
  }, [count, text]);
  const locate = (id: string) => {
    const element = list.current?.querySelector<HTMLElement>('[data-message-id="' + CSS.escape(id) + '"]');
    element?.scrollIntoView({ block: "nearest", behavior: "smooth" }); element?.focus({ preventScroll: true });
  };
  const submit = () => {
    if (composing.current || !text.trim() || !live.canWrite || text.trim().length > 20000) return;
    try {
      const now = Date.now(); const content = text.trim(); let next = live.replica.readThreads();
      let document: ReturnType<typeof live.replica.readRoot> | null = null;
      if (context) {
        const thread = next.find(item => item.id === context.threadId); const original = thread?.messages.find(item => item.id === context.message.id);
        if (!thread || !original || original.deleted) throw new Error("原消息已删除，草稿已保留。可以取消回复后重新留言。");
        if (context.mode === "edit" && (!own(original) || original.text !== context.message.text)) throw new Error("这条消息已变化，请重新打开后修改。你的草稿已保留。");
        next = next.map(item => item.id !== thread.id ? item : { ...item, messages: context.mode === "edit"
          ? item.messages.map(message => message.id === original.id ? { ...message, text: content, editedAt: now } : message)
          : [...item.messages, { id: uuid(), author: profile.displayName, authorId: profile.id, text: content, createdAt: now, replyTo: original.id, deleted: false }] });
      } else {
        const id = uuid(); document = live.replica.readRoot(); const paragraph = target ? findNode(document, target.id) : null;
        if (target && !paragraph) throw new Error("原段落已删除，草稿已保留。可改为文档评论。");
        if (paragraph) paragraph.attrs = { ...paragraph.attrs, writemeCommentIds: [...(paragraph.attrs?.writemeCommentIds ?? []) as string[], id] };
        next = [...next, { id, quote: target?.text.slice(0, 2000) ?? "", anchored: !!target, wholeBlock: !!target,
          messages: [{ id: uuid(), author: profile.displayName, authorId: profile.id, text: content, createdAt: now, deleted: false }] }];
      }
      followEnd.current = true;
      live.replica.doc.transact(() => { if (document && target) live.replica.writeRoot(document); live.replica.writeThreads(next); }, localOrigin);
      live.commentDrafts.delete(key); choose(null); redraw(value => value + 1);
    } catch (error) { setError((error as Error).message); }
  };
  const remove = () => {
    if (!confirm || !live.canWrite) return;
    const all = live.replica.readThreads(); const thread = all.find(item => item.id === confirm.thread.id);
    const message = thread?.messages.find(item => item.id === confirm.message.id);
    if (!thread || !message || !own(message) && live.role !== "owner") { setConfirm(null); return; }
    if (message.id === thread.messages[0].id) live.replica.writeThreads(all.filter(item => item.id !== thread.id));
    else live.replica.writeThreads(all.map(item => item.id === thread.id ? { ...item, messages: item.messages.map(item => item.id === message.id ? { ...item, text: "", deleted: true, editedAt: null } : item) } : item));
    setConfirm(null);
  };
  return createPortal(<div className="shared-app shared-comments-layer" onPointerDown={event => { if (event.target === event.currentTarget) close(); }}>
    <aside ref={panel} className={"shared-comments" + (narrow ? " mobile-sheet" : " paragraph-popover")} style={position} role="dialog" aria-modal={narrow || undefined} aria-label={target ? "段落评论" : "全部评论"} tabIndex={-1}>
      <div className="shared-sheet-grip" aria-hidden="true" />
      <div className="shared-comments-heading"><div><BubbleIcon /><h3>{target ? "段落评论" : "全部评论"}</h3><span>{count}</span></div><button className="shared-icon" aria-label="关闭评论" onClick={close}><Icon name="close" size={18} /></button></div>
      {target && <div className="shared-comment-context"><p>{node ? target.text || "空白段落" : "原段落已删除"}</p><button onClick={all}>全部评论 <span aria-hidden="true">↗</span></button></div>}
      <div className="shared-comment-list" ref={list} onScroll={() => { if (list.current) followEnd.current = list.current.scrollHeight - list.current.scrollTop - list.current.clientHeight < 40; }}>
        {!threads.length && <div className="shared-comment-empty"><BubbleIcon /><h4>写下第一条评论</h4><p>留一个想法，或和伙伴接着聊。</p></div>}
        {threads.map(thread => <section key={thread.id} className="shared-thread" data-thread-id={thread.id}>
          {!target && thread.anchored && <blockquote>{thread.quote || "空白段落"}</blockquote>}
          {thread.messages.filter(message => !message.deleted).map(message => {
            const parent = thread.messages.find(item => item.id === message.replyTo && !item.deleted);
            const person = message.authorId === profile.id ? profile : live.peers.find(item => item.accountId === message.authorId) ?? members.find(item => item.accountId === message.authorId);
            return <div key={message.id} className={"shared-message" + (message.id !== thread.messages[0].id ? " reply" : "")} data-message-id={message.id} tabIndex={-1}>
              <UserAvatar name={person?.displayName ?? message.author} accountId={message.authorId} avatar={person?.avatar} />
              <div className="shared-message-content"><div className="shared-byline"><b>{person?.displayName ?? message.author}</b><time title={new Date(message.createdAt).toLocaleString("zh-CN")}>{relativeTime(message.createdAt)}</time></div>
                {parent && <button className="shared-reply-context" onClick={() => locate(parent.id)}>回复 {parent.author}：{parent.text.slice(0, 80)}</button>}
                <p>{message.text}</p>
                {message.editedAt && <small className="shared-muted">已编辑</small>}
                {live.canWrite && <div className="shared-message-actions">
                  <button onClick={() => { choose({ mode: "reply", threadId: thread.id, message }); input.current?.focus({ preventScroll: true }); }}>回复</button>
                  {own(message) && <button onClick={() => { choose({ mode: "edit", threadId: thread.id, message }); input.current?.focus({ preventScroll: true }); }}>编辑</button>}
                  {(own(message) || live.role === "owner") && <button onClick={() => setConfirm({ thread, message })}>删除</button>}
                </div>}
              </div>
            </div>;
          })}
          {confirm?.thread.id === thread.id && <div className="shared-comment-confirm"><p>{confirm.message.id === thread.messages[0].id ? "删除此讨论及全部回复？" : "删除这条回复？后续回复会保留。"}</p><button className="shared-danger" onClick={remove}>确认删除</button><button className="shared-link" onClick={() => setConfirm(null)}>取消</button></div>}
        </section>)}
      </div>
      <form className="shared-comment-composer" onSubmit={event => { event.preventDefault(); submit(); }}>
        {context && <div className="shared-composer-target"><span>{context.mode === "edit" ? "编辑自己的消息" : "回复 " + context.message.author}</span><button type="button" aria-label="取消回复或编辑" onClick={() => choose(null)}><Icon name="close" size={16} /></button></div>}
        <div className="shared-compose-body"><UserAvatar name={profile.displayName} accountId={profile.id} avatar={profile.avatar} />
          <textarea ref={input} aria-label="评论内容" placeholder={live.canWrite ? context?.mode === "reply" ? "写下你的回复…" : "写下你的想法…" : live.data.syncPaused ? "管理员已暂停写入，草稿会保留" : "你拥有此文档的阅读权限"} value={text} maxLength={20000} readOnly={!live.canWrite}
            onChange={event => draft(event.target.value)} onCompositionStart={() => { composing.current = true; }} onCompositionEnd={() => { composing.current = false; }}
            onKeyDown={event => { if (event.key === "Enter" && (event.ctrlKey || event.metaKey) && !event.nativeEvent.isComposing) { event.preventDefault(); submit(); } }} />
        </div>
        <div className="shared-composer-footer"><small>{narrow ? "回车换行" : "Ctrl + Enter 发送"}</small><button type="submit" className="shared-primary" disabled={!text.trim() || !live.canWrite}>{context?.mode === "edit" ? "保存" : "发送"} <span aria-hidden="true">↑</span></button></div>
        {error && <p className="shared-error" role="alert">{error}</p>}
      </form>
    </aside>
  </div>, document.body);
}
