import type { Editor } from "@tiptap/core";
import type { Node as ProseMirrorNode } from "@tiptap/pm/model";
import { useEffect, useRef, type RefObject } from "react";
import { childPosition, moveBlockTransaction, type BlockAddress, type BlockDestination } from "./blockTree";

// Note: 自研指针拖拽、可见层级命中与同一落点计算 — 见 .agents/notes/implemented/feature/2026-09-08-m2-block-drag.md

interface BlockRow extends BlockAddress {
  el: HTMLElement;
  top: number;
  bottom: number;
  fullBottom: number;
  left: number;
  contentLeft: number;
  right: number;
}

interface DropTarget {
  destination: BlockDestination;
  row: BlockRow;
  inside: boolean;
  top: number;
  left: number;
  right: number;
}

function readRows(editor: Editor): BlockRow[] {
  const rows: BlockRow[] = [];
  const walk = (parent: ProseMirrorNode, parentPos: number) => {
    parent.forEach((node, offset, index) => {
      if (parent.type.name === "toggleBlock" && index === 0) return;
      const pos = parentPos + 1 + offset;
      const el = editor.view.nodeDOM(pos);
      if (!(el instanceof HTMLElement)) return;
      const rect = el.getBoundingClientRect();
      if (!rect.height || !rect.width) return;
      const title = node.type.name === "toggleBlock"
        ? el.querySelector(":scope > .wm-toggle-content > p")?.getBoundingClientRect()
        : null;
      rows.push({ node, pos, parentPos, index, el, top: title?.top ?? rect.top,
        bottom: title?.bottom ?? rect.bottom, fullBottom: rect.bottom, left: rect.left,
        contentLeft: title?.left ?? rect.left, right: rect.right });
      if (node.type.name === "toggleBlock" && !node.attrs.collapsed) walk(node, pos);
    });
  };
  walk(editor.state.doc, -1);
  return rows;
}

function nearestRow(rows: BlockRow[], y: number): BlockRow | undefined {
  let nearest: BlockRow | undefined;
  let distance = Infinity;
  for (const row of rows) {
    const gap = Math.max(row.top - y, y - row.bottom, 0);
    if (gap < distance) { distance = gap; nearest = row; }
  }
  return nearest;
}

/** 指示线与提交共用这份结果，不能各自猜一次落点。 */
function findDrop(editor: Editor, rows: BlockRow[], source: BlockRow, x: number, y: number): DropTarget | null {
  const end = source.pos + source.node.nodeSize;
  if (y >= source.top && y <= source.fullBottom && x >= source.left - 8) return null;
  const candidates = rows.filter((row) => row.pos < source.pos || row.pos >= end);
  let row = nearestRow(candidates, y);
  if (!row) return null;

  // 向左拖过一级缩进时，落点提升到该父块之外，而不是仍留在子树里。
  while (row.parentPos >= 0 && x < row.left - 10) {
    const parent = rows.find((item) => item.pos === row?.parentPos);
    if (!parent) break;
    row = parent;
  }
  const ratio = (y - row.top) / Math.max(row.bottom - row.top, 1);
  const inside = row.node.type.name === "toggleBlock" && ratio > 0.2 && ratio < 0.8 && x >= row.contentLeft - 4;
  const after = y >= (row.top + row.bottom) / 2;
  const destination = inside
    ? { parentPos: row.pos, index: 1 }
    : { parentPos: row.parentPos, index: row.index + (after ? 1 : 0) };
  const pos = childPosition(editor.state.doc, destination);
  if (pos === null || (pos >= source.pos && pos <= end) ||
      (destination.parentPos >= source.pos && destination.parentPos < end)) return null;
  return { destination, row, inside, top: inside ? row.bottom + 2 : after ? row.fullBottom + 2 : row.top - 2,
    left: inside ? row.contentLeft : row.left, right: row.right };
}

interface Props {
  editor: Editor | null;
  scopeRef: RefObject<HTMLDivElement | null>;
  onOpenMenu: (pos: number, point: { left: number; top: number }) => void;
}

export default function BlockHandle({ editor, scopeRef, onOpenMenu }: Props) {
  const handleRef = useRef<HTMLButtonElement>(null);
  const openMenuRef = useRef(onOpenMenu);
  openMenuRef.current = onOpenMenu;

  useEffect(() => {
    const handle = handleRef.current;
    const scope = scopeRef.current;
    if (!editor || !scope || !handle) return;
    const scroller = scope.closest<HTMLElement>(".editor");
    let hovered: BlockRow | undefined;
    let animation = 0;
    let active: {
      source: BlockRow; doc: ProseMirrorNode; pointer: number; startX: number; startY: number;
      x: number; y: number; moved: boolean; ghost: HTMLElement | null; line: HTMLElement | null;
      target: DropTarget | null;
    } | null = null;

    const hide = () => {
      handle.style.opacity = "0";
      handle.style.pointerEvents = "none";
      hovered = undefined;
    };
    const cleanup = () => {
      const previous = active;
      active = null;
      cancelAnimationFrame(animation);
      previous?.source.el.classList.remove("wm-drag-source");
      previous?.target?.row.el.classList.remove("wm-drop-target");
      previous?.ghost?.remove();
      previous?.line?.remove();
      scope.classList.remove("wm-is-dragging");
      if (previous && handle.hasPointerCapture(previous.pointer)) handle.releasePointerCapture(previous.pointer);
      hide();
    };
    const updateTarget = () => {
      if (!active?.line) return;
      const rows = readRows(editor);
      const source = rows.find((row) => row.pos === active?.source.pos);
      active.target?.row.el.classList.remove("wm-drop-target");
      const bounds = scroller?.getBoundingClientRect();
      const outside = bounds && (active.x < bounds.left || active.x > bounds.right || active.y < bounds.top || active.y > bounds.bottom);
      active.target = source && !outside ? findDrop(editor, rows, source, active.x, active.y) : null;
      const { target, line } = active;
      line.hidden = !target;
      if (!target) return;
      line.style.top = `${target.top}px`;
      line.style.left = `${target.left}px`;
      line.style.width = `${Math.max(target.right - target.left, 24)}px`;
      line.dataset.kind = target.inside ? "inside" : "between";
      line.dataset.label = target.inside ? "移入折叠块" : "";
      if (target.inside) target.row.el.classList.add("wm-drop-target");
    };
    const tick = () => {
      if (!active?.moved) return;
      if (scroller) {
        const rect = scroller.getBoundingClientRect();
        const edge = 56;
        const direction = active.y < rect.top + edge ? -1 : active.y > rect.bottom - edge ? 1 : 0;
        if (direction) {
          scroller.scrollTop += direction * 12;
          updateTarget();
        }
      }
      animation = requestAnimationFrame(tick);
    };
    const onHover = (event: MouseEvent) => {
      if (active || handle.contains(event.target as globalThis.Node)) return;
      if ((event.target as Element).closest(".wm-block-menu, .wm-slash-menu, .wm-format-popover")) { hide(); return; }
      const row = nearestRow(readRows(editor), event.clientY);
      if (!row || event.clientY < row.top - 12 || event.clientY > row.bottom + 16) { hide(); return; }
      hovered = row;
      const rect = scope.getBoundingClientRect();
      handle.style.left = `${row.left - rect.left - 28}px`;
      handle.style.top = `${row.top - rect.top}px`;
      handle.style.height = `${Math.min(row.bottom - row.top, 32)}px`;
      handle.style.opacity = "1";
      handle.style.pointerEvents = "auto";
    };
    const onLeave = (event: MouseEvent) => {
      if (!active && !scope.contains(event.relatedTarget as globalThis.Node | null)) hide();
    };
    const onDown = (event: PointerEvent) => {
      if (event.button !== 0 || !hovered) return;
      event.preventDefault();
      const source = readRows(editor).find((row) => row.pos === hovered?.pos);
      if (!source) return;
      active = { source, doc: editor.state.doc, pointer: event.pointerId, startX: event.clientX, startY: event.clientY,
        x: event.clientX, y: event.clientY, moved: false, ghost: null, line: null, target: null };
      handle.setPointerCapture(event.pointerId);
    };
    const onMove = (event: PointerEvent) => {
      if (!active || event.pointerId !== active.pointer) return;
      active.x = event.clientX;
      active.y = event.clientY;
      if (!active.moved) {
        if (Math.hypot(active.x - active.startX, active.y - active.startY) < 5) return;
        active.moved = true;
        const ghost = document.createElement("div");
        ghost.className = "wm-drag-ghost";
        ghost.setAttribute("aria-hidden", "true");
        const body = document.createElement("div");
        body.className = "writeme-editor wm-ghost-editor";
        body.style.width = `${active.source.right - active.source.left}px`;
        const clone = active.source.el.cloneNode(true) as HTMLElement;
        clone.classList.remove("ProseMirror-selectednode");
        clone.removeAttribute("id");
        clone.querySelectorAll("[id]").forEach((element) => element.removeAttribute("id"));
        body.append(clone);
        ghost.append(body);
        const line = document.createElement("div");
        line.className = "wm-drop-line";
        line.setAttribute("aria-hidden", "true");
        document.body.append(ghost, line);
        active.ghost = ghost;
        active.line = line;
        active.source.el.classList.add("wm-drag-source");
        scope.classList.add("wm-is-dragging");
        animation = requestAnimationFrame(tick);
      }
      if (active.ghost) active.ghost.style.transform = `translate(${active.x + 16}px, ${active.y - 14}px)`;
      updateTarget();
    };
    const onUp = (event: PointerEvent) => {
      if (!active || event.pointerId !== active.pointer) return;
      const finished = active;
      if (finished.moved) updateTarget();
      const target = finished.target;
      cleanup();
      if (!finished.moved) {
        openMenuRef.current(finished.source.pos, { left: finished.source.left, top: finished.source.top });
      } else if (target && editor.state.doc === finished.doc) {
        const tr = moveBlockTransaction(editor.state, finished.source.pos, target.destination);
        if (tr) { editor.view.dispatch(tr); editor.view.focus(); }
      }
    };
    const onKey = (event: KeyboardEvent) => {
      if (event.key === "Escape" && active) { event.preventDefault(); cleanup(); }
      else if ((event.key === "Enter" || event.key === " ") && event.target === handle && hovered) {
        event.preventDefault();
        openMenuRef.current(hovered.pos, { left: hovered.left, top: hovered.top });
      }
    };
    const onTransaction = () => {
      if (active && editor.state.doc !== active.doc) cleanup();
      else if (!active) hide();
    };
    const onScroll = () => { if (!active) hide(); };
    scope.addEventListener("mousemove", onHover);
    scope.addEventListener("mouseleave", onLeave);
    handle.addEventListener("pointerdown", onDown);
    handle.addEventListener("lostpointercapture", cleanup);
    window.addEventListener("pointermove", onMove);
    window.addEventListener("pointerup", onUp);
    window.addEventListener("pointercancel", cleanup);
    window.addEventListener("blur", cleanup);
    window.addEventListener("keydown", onKey, true);
    scroller?.addEventListener("scroll", onScroll);
    editor.on("transaction", onTransaction);
    return () => {
      cleanup();
      scope.removeEventListener("mousemove", onHover);
      scope.removeEventListener("mouseleave", onLeave);
      handle.removeEventListener("pointerdown", onDown);
      handle.removeEventListener("lostpointercapture", cleanup);
      window.removeEventListener("pointermove", onMove);
      window.removeEventListener("pointerup", onUp);
      window.removeEventListener("pointercancel", cleanup);
      window.removeEventListener("blur", cleanup);
      window.removeEventListener("keydown", onKey, true);
      scroller?.removeEventListener("scroll", onScroll);
      editor.off("transaction", onTransaction);
    };
  }, [editor, scopeRef]);

  return <button type="button" className="wm-handle" ref={handleRef} tabIndex={-1}
    aria-label="拖动块或打开块菜单" title="拖动以移动，单击打开块菜单">
    <svg width="12" height="16" viewBox="0 0 12 16" aria-hidden="true" fill="currentColor">
      {[3, 8, 13].map((y) => <g key={y}><circle cx="3" cy={y} r="1.25" /><circle cx="8" cy={y} r="1.25" /></g>)}
    </svg>
  </button>;
}
