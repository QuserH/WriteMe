import { useCallback, useEffect, useRef, useState } from "react";
import EditorBlock from "./editor/Editor";
import Icon from "./components/Icon";
import {
  createDocument,
  deleteDocument,
  getDocument,
  isDesktop,
  listDocuments,
  updateDocument,
} from "./lib/api";
import type { Doc, DocMeta, SaveState } from "./types";

const AUTOSAVE_DELAY = 800; // ms

// Note: 文档初始化和保存边界 — 见 .agents/notes/implemented/feature/2026-09-08-m0-skeleton-local-autosave.md

export default function App() {
  const [docs, setDocs] = useState<DocMeta[]>([]);
  const [selectedId, setSelectedId] = useState<string | null>(null);
  const [current, setCurrent] = useState<Doc | null>(null);
  const [title, setTitle] = useState("");
  const [content, setContent] = useState("");
  const [saveState, setSaveState] = useState<SaveState>("idle");
  const [lastSavedAt, setLastSavedAt] = useState<number | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [booted, setBooted] = useState(false);
  const [sidebarOpen, setSidebarOpen] = useState(true);
  const initializedRef = useRef(false);

  // 保存用的最新值引用，避免异步闭包拿到过期数据
  const snap = useRef({ id: "", title: "", content: "" });
  const savingRef = useRef(false);
  const dirtyRef = useRef(false);
  const bootRef = useRef(false); // 首帧不触发保存
  bootRef.current = true;

  snap.current = { id: selectedId ?? "", title, content };

  const doSave = useCallback(async () => {
    const s = snap.current;
    if (!s.id) return;
    if (savingRef.current) return;
    dirtyRef.current = false;
    savingRef.current = true;
    setSaveState("saving");
    try {
      await updateDocument(s.id, s.title, s.content);
      setSaveState("saved");
      setLastSavedAt(Date.now());
      // 刷新侧栏的标题/时间（保持当前选中）
      const list = await listDocuments();
      setDocs(list);
    } catch (e) {
      dirtyRef.current = true;
      setSaveState("error");
      setError(String(e));
    } finally {
      savingRef.current = false;
    }
  }, []);

  // 切换文档时先落盘再加载
  const openDocument = useCallback(
    async (id: string) => {
      if (dirtyRef.current) await doSave();
      setSaveState("idle");
      const doc = await getDocument(id);
      if (!doc) return;
      setCurrent(doc);
      setTitle(doc.title);
      setContent(doc.content);
      setSelectedId(doc.id);
      setError(null);
    },
    [doSave],
  );

  const handleCreate = useCallback(async () => {
    if (dirtyRef.current) await doSave();
    const meta = await createDocument(crypto.randomUUID(), "未命名文档");
    setDocs((prev) => [meta, ...prev]);
    await openDocument(meta.id);
  }, [doSave, openDocument]);

  const handleDelete = useCallback(async () => {
    if (!current) return;
    const ok = window.confirm(`确定删除「${current.title || "未命名文档"}」吗？此操作不可恢复。`);
    if (!ok) return;
    await deleteDocument(current.id);
    setCurrent(null);
    setTitle("");
    setContent("");
    const rest = docs.filter((d) => d.id !== current.id);
    setDocs(rest);
    if (rest.length > 0) {
      await openDocument(rest[0].id);
    } else {
      setSelectedId(null);
    }
  }, [current, docs, openDocument]);

  // 初始化：载入文档列表，空库则新建一篇
  useEffect(() => {
    if (initializedRef.current) return;
    initializedRef.current = true;
    (async () => {
      try {
        let list = await listDocuments();
        if (list.length === 0) {
          const meta = await createDocument(crypto.randomUUID(), "欢迎使用 WriteME");
          list = [meta];
          setDocs(list);
        } else {
          setDocs(list);
        }
        await openDocument(list[0].id);
      } catch (e) {
        setError(String(e));
      } finally {
        setBooted(true);
      }
    })();
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, []);

  // 防抖自动保存
  useEffect(() => {
    if (!booted || !selectedId) return;
    if (!bootRef.current) {
      bootRef.current = true;
      return;
    }
    dirtyRef.current = true;
    const timer = setTimeout(() => {
      void doSave();
    }, AUTOSAVE_DELAY);
    return () => clearTimeout(timer);
  }, [title, content, selectedId, booted, doSave]);

  // 窗口关闭前尽力保存
  useEffect(() => {
    const flush = () => {
      if (dirtyRef.current) void doSave();
    };
    window.addEventListener("beforeunload", flush);
    return () => window.removeEventListener("beforeunload", flush);
  }, [doSave]);

  const fmtTime = (t: number) =>
    new Date(t).toLocaleTimeString("zh-CN", { hour12: false });

  return (
    <div className={`shell${sidebarOpen ? "" : " sidebar-collapsed"}`}>
      <aside className="sidebar" aria-label="文档侧栏" hidden={!sidebarOpen}>
        <div className="brand">
          <span className="brand-mark">W</span>
          <div className="brand-copy">
            <span className="brand-name">WriteME</span>
            <span className="space-name">个人空间</span>
          </div>
        </div>

        <button type="button" className="new-document" onClick={() => void handleCreate()}>
          <Icon name="plus" size={17} />新建文档
        </button>

        <div className="doc-section">
          <div className="doc-section-head">
            <span>全部文档</span>
            <span className="doc-count">{docs.length}</span>
          </div>
          <ul className="doc-list">
            {docs.map((d) => (
              <li key={d.id}>
                <button
                  className={`doc-item ${d.id === selectedId ? "active" : ""}`}
                  onClick={() => void openDocument(d.id)}
                  title={d.title}
                  aria-current={d.id === selectedId ? "page" : undefined}
                >
                  <span className="doc-item-icon"><Icon name="file" /></span>
                  <span className="doc-item-copy">
                    <span className="doc-item-title">{d.title || "未命名文档"}</span>
                    <span className="doc-item-time">
                      {new Date(d.updated_at).toLocaleDateString("zh-CN")}
                    </span>
                  </span>
                </button>
              </li>
            ))}
          </ul>
        </div>
        <div className="sidebar-footer"><Icon name="device" size={15} />
          <span>{isDesktop ? "保存在此设备" : "浏览器演示"}</span>
        </div>
      </aside>

      <main className="main">
        <header className="document-topbar">
          <button type="button" className="icon-btn" aria-label={sidebarOpen ? "收起侧栏" : "展开侧栏"}
            title={sidebarOpen ? "收起侧栏" : "展开侧栏"} onClick={() => setSidebarOpen((open) => !open)}>
            <Icon name="panel" />
          </button>
          <div className="document-breadcrumb"><span>文档</span><Icon name="chevron" size={12} />
            <span className="breadcrumb-title">{current ? title || "未命名文档" : "个人空间"}</span>
          </div>
        </header>
        {error && (
          <div className="error-banner">
            <span>{error}</span>
            <button onClick={() => setError(null)}>✕</button>
          </div>
        )}

        {!booted ? (
          <div className="center-hint">正在加载…</div>
        ) : !current ? (
          <div className="empty-state">
            <h1>从一个想法开始</h1>
            <p>新建一篇文档，记录今天的想法。</p>
            <button type="button" className="new-document" onClick={() => void handleCreate()}><Icon name="plus" />新建文档</button>
          </div>
        ) : (
          <div className="editor-wrap">
            <div className="editor">
              <input
                className="doc-title"
                aria-label="文档标题"
                value={title}
                placeholder="未命名文档"
                onChange={(e) => setTitle(e.target.value)}
              />
              {/* M1 块级编辑器（TipTap）：正文持久化为文档 JSON；key 确保切换文档重建 */}
              <EditorBlock
                key={current.id}
                contentRaw={content}
                onContentChange={setContent}
              />
            </div>
            <footer className="statusbar">
              <span>{docs.length} 篇文档</span>
              <span className="status-spacer" />
              {saveState === "saving" && <span className="muted">正在保存…</span>}
              {saveState === "error" && <span className="err">保存失败</span>}
              {saveState === "saved" && lastSavedAt && (
                <span className="muted">已保存 {fmtTime(lastSavedAt)}</span>
              )}
              <button type="button" className="status-delete" onClick={() => void handleDelete()}>
                删除文档
              </button>
            </footer>
          </div>
        )}
      </main>
    </div>
  );
}
