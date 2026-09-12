import { useEffect, useState } from "react";

// Note: 安卓软键盘改变 visualViewport；弹层和输入框使用真实可见高度 — 见 .agents/notes/implemented/architecture/2026-09-12-shared-workspaces-and-realtime.md
export function useSharedViewport() {
  useEffect(() => {
    const viewport = window.visualViewport;
    let frame = 0;
    const measure = () => {
      frame = 0;
      const height = viewport && viewport.scale === 1 ? viewport.height : innerHeight;
      const top = viewport && viewport.scale === 1 ? viewport.offsetTop : 0;
      document.documentElement.style.setProperty("--shared-visible-height", `${height}px`);
      document.documentElement.style.setProperty("--shared-visible-top", `${top}px`);
    };
    const schedule = () => { if (!frame) frame = requestAnimationFrame(measure); };
    viewport?.addEventListener("resize", schedule); viewport?.addEventListener("scroll", schedule); window.addEventListener("resize", schedule); measure();
    return () => { cancelAnimationFrame(frame); viewport?.removeEventListener("resize", schedule); viewport?.removeEventListener("scroll", schedule); window.removeEventListener("resize", schedule); document.documentElement.style.removeProperty("--shared-visible-height"); document.documentElement.style.removeProperty("--shared-visible-top"); };
  }, []);
}
export function useNarrowViewport(maximum = 700) {
  const [narrow, setNarrow] = useState(() => matchMedia(`(max-width:${maximum}px)`).matches);
  useEffect(() => { const media = matchMedia(`(max-width:${maximum}px)`); const update = () => setNarrow(media.matches); media.addEventListener("change", update); update(); return () => media.removeEventListener("change", update); }, [maximum]);
  return narrow;
}
